using System.Globalization;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Leaderboard;

/// <summary>
/// Boards (<c>all</c> and each format) by period (<c>all</c>, month, ISO week): a ledger of points and
/// abandonment penalties, and a score derived from it in <c>(at, session_id)</c> order (design 13).
/// Core has already locked every seat's player in ascending id and skips this handler's marker for
/// repeated deliveries; all writes here are still idempotent.
/// </summary>
public sealed class LeaderboardFeature : IFeature
{
    private static readonly string[] FormatEndings = ["completed", "forfeit", "no_contest"];

    public string Id => "leaderboard";

    public FeatureSettings Settings => FeatureSettings.Create(new LeaderboardSettings(), static _ => []);

    public void Configure(IFeatureBuilder feature)
    {
        feature.On<SessionEnded>(ProjectSessionEndedAsync);
        feature.CleanupPlayer(CleanupPlayerAsync);
        feature.PlayerRoutes(LeaderboardReadRoutes.MapPlayerRoutes);
        feature.Provides<long>("leaderboard.score", LeaderboardReadRoutes.GetScoreAsync);
        feature.Uses(LeaderboardReadRoutes.FriendsQuery);
    }

    private static async Task ProjectSessionEndedAsync(ITenantTx transaction, SessionEnded @event, CancellationToken cancellationToken)
    {
        var session = await transaction.QuerySingleOrDefaultAsync<SessionRow>("""
            select s.format as Format,
                   s.ended_at as EndedAt,
                   s.ended_tz as EndedTz,
                   s.end_reason as EndReason
            from sessions s
            where s.id = @sessionId and s.ended_at is not null
            """, new { sessionId = @event.SessionId }, cancellationToken);
        if (session is null || session.EndReason is null || !FormatEndings.Contains(session.EndReason)) return;

        var seats = await transaction.QueryAsync<SeatRow>("""
            select p.player_id as PlayerId,
                   p.score as Score,
                   coalesce(p.ranked, false) as Ranked,
                   p.eligible as Eligible,
                   coalesce((p.result ->> 'abandoned')::boolean, false) as Abandoned
            from session_participants p
            join players player on player.id = p.player_id
            where p.session_id = @sessionId
              and p.role = 'player'
              and player.deleted_at is null
            order by p.player_id
            """, new { sessionId = @event.SessionId }, cancellationToken);

        var keys = Keys(session);
        foreach (var seat in seats)
        {
            if (!seat.Eligible) continue;
            if (seat.Abandoned)
            {
                await ApplyAbandonmentAsync(transaction, seat.PlayerId, @event.SessionId, session, keys, cancellationToken);
            }
            else if (seat.Ranked)
            {
                await ApplyPointsAsync(transaction, seat.PlayerId, @event.SessionId, session.EndedAt, Math.Max(0, seat.Score), keys, cancellationToken);
            }
        }
    }

    private static async Task ApplyPointsAsync(
        ITenantTx transaction, Guid playerId, Guid sessionId, DateTimeOffset at, int points, IReadOnlyList<BoardPeriod> keys, CancellationToken cancellationToken)
    {
        var inserted = 0;
        foreach (var key in keys)
        {
            inserted += await InsertEntryAsync(transaction, playerId, sessionId, key, "points", at, points, cancellationToken);
        }

        if (inserted == 0) return;
        foreach (var key in keys)
        {
            await ApplyOrRefoldAsync(transaction, playerId, sessionId, at, key, points, 0, [], cancellationToken);
        }
    }

    private static async Task ApplyAbandonmentAsync(
        ITenantTx transaction, Guid playerId, Guid sessionId, SessionRow session, IReadOnlyList<BoardPeriod> keys, CancellationToken cancellationToken)
    {
        var at = session.EndedAt;
        var inserted = await transaction.ExecuteAsync("""
            insert into leaderboard_abandonments (tenant_id, player_id, session_id, format, ended_at, ended_tz, amount)
            values (app_tenant(), @playerId, @sessionId, @format, @at, @tz, 0)
            on conflict do nothing
            """, new { playerId, sessionId, format = session.Format, at, tz = session.EndedTz ?? "UTC" }, cancellationToken);
        if (inserted == 0) return;

        // Everything that could be affected: this abandonment and every later one whose seven-day window reaches it.
        var neighbours = (await transaction.QueryAsync<AbandonmentRow>("""
            select session_id as SessionId, ended_at as EndedAt, amount as Amount
            from leaderboard_abandonments
            where player_id = @playerId
              and ended_at > @from and ended_at < @to
            order by ended_at, session_id
            """, new { playerId, from = at - AbandonmentRules.Window, to = at + AbandonmentRules.Window }, cancellationToken)).ToList();

        var own = neighbours.FindIndex(row => row.SessionId == sessionId);
        var changed = new List<Guid>();
        var ownAmount = 0;
        for (var index = own; index < neighbours.Count; index++)
        {
            var target = neighbours[index];
            var windowStart = target.EndedAt - AbandonmentRules.Window;
            var occurrence = 0;
            for (var earlier = 0; earlier <= index; earlier++)
            {
                if (neighbours[earlier].EndedAt > windowStart) occurrence++;
            }

            var amount = AbandonmentRules.Penalty(occurrence);
            if (index == own) ownAmount = amount;
            if (amount == target.Amount && index != own) continue;

            await transaction.ExecuteAsync(
                "update leaderboard_abandonments set amount = @amount where player_id = @playerId and session_id = @sessionId",
                new { amount, playerId, sessionId = target.SessionId }, cancellationToken);
            if (index != own)
            {
                await transaction.ExecuteAsync(
                    "update leaderboard_entries set amount = @amount where player_id = @playerId and session_id = @sessionId and kind = 'penalty'",
                    new { amount, playerId, sessionId = target.SessionId }, cancellationToken);
                changed.Add(target.SessionId);
            }
        }

        foreach (var key in keys)
        {
            await InsertEntryAsync(transaction, playerId, sessionId, key, "penalty", at, ownAmount, cancellationToken);
        }

        var forced = new HashSet<BoardPeriod>();
        if (changed.Count > 0)
        {
            var affected = await transaction.QueryAsync<BoardPeriod>("""
                select distinct board as Board, period as Period
                from leaderboard_entries
                where player_id = @playerId and session_id = any(@changed) and kind = 'penalty'
                """, new { playerId, changed = changed.ToArray() }, cancellationToken);
            forced.UnionWith(affected);
        }

        foreach (var key in keys)
        {
            await ApplyOrRefoldAsync(transaction, playerId, sessionId, at, key, 0, ownAmount, forced, cancellationToken);
        }

        foreach (var key in forced.Except(keys)) await RefoldAsync(transaction, playerId, key, cancellationToken);
    }

    private static async Task ApplyOrRefoldAsync(
        ITenantTx transaction, Guid playerId, Guid sessionId, DateTimeOffset at, BoardPeriod key, int points, int penalty,
        HashSet<BoardPeriod> forced, CancellationToken cancellationToken)
    {
        var isLate = forced.Contains(key) || await transaction.QuerySingleOrDefaultAsync<bool>("""
            select exists(
                select 1 from leaderboard_scores
                where player_id = @playerId and board = @board and period = @period
                  and (last_at, last_session) > (@at, @sessionId))
            """, new { playerId, board = key.Board, period = key.Period, at, sessionId }, cancellationToken);
        if (isLate)
        {
            await RefoldAsync(transaction, playerId, key, cancellationToken);
            return;
        }

        await transaction.ExecuteAsync("""
            insert into leaderboard_scores (tenant_id, board, period, player_id, score, last_at, last_session)
            values (app_tenant(), @board, @period, @playerId, greatest(0, greatest(0, @points) - @penalty), @at, @sessionId)
            on conflict (tenant_id, board, period, player_id) do update set
                score = greatest(0, greatest(0, leaderboard_scores.score + @points) - @penalty),
                last_at = @at,
                last_session = @sessionId
            """, new { playerId, board = key.Board, period = key.Period, points, penalty, at, sessionId }, cancellationToken);
    }

    /// <summary>Folds the whole ledger of one board and period in time order, points before penalty within a session.</summary>
    private static async Task RefoldAsync(ITenantTx transaction, Guid playerId, BoardPeriod key, CancellationToken cancellationToken)
    {
        var entries = await transaction.QueryAsync<EntryRow>("""
            select session_id as SessionId, kind as Kind, at as At, amount as Amount
            from leaderboard_entries
            where player_id = @playerId and board = @board and period = @period
            order by at, session_id, case kind when 'points' then 0 else 1 end
            """, new { playerId, board = key.Board, period = key.Period }, cancellationToken);

        long score = 0;
        EntryRow? last = null;
        foreach (var entry in entries)
        {
            score = entry.Kind == "points" ? score + entry.Amount : Math.Max(0, score - entry.Amount);
            last = entry;
        }

        if (last is null) return;
        await transaction.ExecuteAsync("""
            insert into leaderboard_scores (tenant_id, board, period, player_id, score, last_at, last_session)
            values (app_tenant(), @board, @period, @playerId, @score, @at, @sessionId)
            on conflict (tenant_id, board, period, player_id) do update set
                score = excluded.score, last_at = excluded.last_at, last_session = excluded.last_session
            """, new { playerId, board = key.Board, period = key.Period, score, at = last.At, sessionId = last.SessionId }, cancellationToken);
    }

    private static Task<int> InsertEntryAsync(
        ITenantTx transaction, Guid playerId, Guid sessionId, BoardPeriod key, string kind, DateTimeOffset at, int amount, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("""
            insert into leaderboard_entries (tenant_id, board, period, player_id, session_id, kind, at, amount)
            values (app_tenant(), @board, @period, @playerId, @sessionId, @kind, @at, @amount)
            on conflict do nothing
            """, new { board = key.Board, period = key.Period, playerId, sessionId, kind, at, amount }, cancellationToken);

    private static IReadOnlyList<BoardPeriod> Keys(SessionRow session)
    {
        var local = TimeZoneInfo.ConvertTime(session.EndedAt, ResolveZone(session.EndedTz));
        var week = $"{ISOWeek.GetYear(local.DateTime):0000}-W{ISOWeek.GetWeekOfYear(local.DateTime):00}";
        var month = local.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var boards = new[] { "all", session.Format }.Distinct(StringComparer.Ordinal);
        return [.. boards.SelectMany(board => new[] { "all", month, week }.Select(period => new BoardPeriod(board, period)))];
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrEmpty(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private static Task CleanupPlayerAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("""
            delete from leaderboard_entries where player_id = @playerId;
            delete from leaderboard_scores where player_id = @playerId;
            delete from leaderboard_abandonments where player_id = @playerId;
            """, new { playerId }, cancellationToken);

    private sealed record LeaderboardSettings;

    private sealed record SessionRow(string Format, DateTimeOffset EndedAt, string? EndedTz, string? EndReason);

    private sealed record SeatRow(Guid PlayerId, int Score, bool Ranked, bool Eligible, bool Abandoned);

    private sealed record AbandonmentRow(Guid SessionId, DateTimeOffset EndedAt, int Amount);

    private sealed record EntryRow(Guid SessionId, string Kind, DateTimeOffset At, int Amount);

    private sealed record BoardPeriod(string Board, string Period);
}
