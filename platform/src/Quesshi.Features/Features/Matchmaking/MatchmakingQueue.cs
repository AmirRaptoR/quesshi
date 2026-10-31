using System.Text.Json;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Features.Matchmaking;

public enum EnqueueOutcome { Queued, InvalidSettings, FormatUnavailable, PlayerUnavailable, AlreadyPlaying }

/// <summary>
/// The queue and its pairing attempt. A pairing attempt finds a candidate without locking, then locks both
/// players (ascending), re-reads both entries and, in the same transaction, removes them and starts the session.
/// </summary>
internal sealed class MatchmakingQueue(ITenantDb database, ILocks locks, ISessions sessions, IInbox inbox)
{
    private const int AttemptsPerRequest = 3;
    private const int SweepBatch = 1000;
    private static readonly JsonElement EmptyRules = JsonDocument.Parse("{}").RootElement.Clone();

    public async Task<EnqueueOutcome> EnqueueAsync(
        Guid tenantId, Guid playerId, string format, JsonElement settings, CancellationToken cancellationToken)
    {
        var outcome = await database.InTenantAsync(tenantId, async transaction =>
        {
            await locks.PlayersAsync(transaction, [playerId], cancellationToken);
            var language = await transaction.QuerySingleOrDefaultAsync<string>("""
                select lang from players where id = @playerId and deleted_at is null and banned_at is null
                """, new { playerId }, cancellationToken);
            if (language is null) return EnqueueOutcome.PlayerUnavailable;

            var enabled = await transaction.QuerySingleOrDefaultAsync<bool?>("""
                select entitled and enabled from tenant_capabilities where type = 'format' and key = @format
                """, new { format }, cancellationToken);
            if (enabled != true) return EnqueueOutcome.FormatUnavailable;

            var parsed = MatchmakingRules.ParseLive(settings, language);
            if (parsed is null) return EnqueueOutcome.InvalidSettings;

            // A player already in a running duel of this format is not queued a second time.
            var playing = await transaction.QuerySingleOrDefaultAsync<bool>("""
                select exists (
                    select 1
                    from session_participants participant
                    join sessions session on session.tenant_id = participant.tenant_id and session.id = participant.session_id
                    where participant.player_id = @playerId and participant.status = 'active'
                      and session.format = @format and session.status = 'running')
                """, new { playerId, format }, cancellationToken);
            if (playing) return EnqueueOutcome.AlreadyPlaying;

            await transaction.ExecuteAsync("""
                insert into matchmaking_entries (tenant_id, player_id, format, match_key, settings)
                values (current_setting('app.tenant_id')::uuid, @playerId, @format, @matchKey, @settings)
                on conflict (tenant_id, player_id, format) do update
                set match_key = excluded.match_key,
                    settings = excluded.settings,
                    enqueued_at = case when matchmaking_entries.match_key = excluded.match_key
                                       then matchmaking_entries.enqueued_at else now() end
                """, new
            {
                playerId,
                format,
                matchKey = MatchmakingRules.MatchKey(format, parsed),
                settings = new Jsonb<LiveQueueSettings>(parsed, MatchmakingJsonContext.Default.LiveQueueSettings),
            }, cancellationToken);
            inbox.Changed(transaction, playerId);
            return EnqueueOutcome.Queued;
        }, cancellationToken: cancellationToken);

        if (outcome == EnqueueOutcome.Queued) await AttemptAsync(tenantId, playerId, format, cancellationToken);
        return outcome;
    }

    public Task LeaveAsync(Guid tenantId, Guid playerId, string format, CancellationToken cancellationToken) =>
        database.InTenantAsync(tenantId, async transaction =>
        {
            await locks.PlayersAsync(transaction, [playerId], cancellationToken);
            var removed = await transaction.ExecuteAsync("""
                delete from matchmaking_entries where player_id = @playerId and format = @format
                """, new { playerId, format }, cancellationToken);
            if (removed > 0) inbox.Changed(transaction, playerId);
            return removed;
        }, cancellationToken: cancellationToken);

    /// <summary>Tries to pair one waiting player with the longest-waiting compatible opponent.</summary>
    public async Task AttemptAsync(Guid tenantId, Guid playerId, string format, CancellationToken cancellationToken)
    {
        // The player's entry is already committed; a failed attempt is retried by the scheduled sweep.
        try
        {
            for (var attempt = 0; attempt < AttemptsPerRequest; attempt++)
            {
                var candidate = await database.InTenantAsync(tenantId, transaction => transaction.QuerySingleOrDefaultAsync<Candidate>("""
                    select other.player_id as OpponentId, mine.match_key as MatchKey
                    from matchmaking_entries mine
                    join matchmaking_entries other on other.tenant_id = mine.tenant_id and other.format = mine.format
                                                  and other.match_key = mine.match_key and other.session_id is null
                                                  and other.player_id <> mine.player_id
                    where mine.player_id = @playerId and mine.format = @format and mine.session_id is null
                    order by other.enqueued_at, other.player_id
                    limit 1
                    """, new { playerId, format }, cancellationToken), readOnly: true, cancellationToken: cancellationToken);
                if (candidate is null) return;
                if (await TryPairAsync(tenantId, format, candidate.MatchKey, playerId, candidate.OpponentId, cancellationToken)) return;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not LockOrderViolation)
        {
        }
    }

    /// <summary>Pairs everything still waiting; one failing pair never blocks the others.</summary>
    public async Task SweepAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var waiting = await database.InTenantAsync(tenantId, transaction => transaction.QueryAsync<Waiting>("""
            select player_id as PlayerId, format as Format, match_key as MatchKey
            from matchmaking_entries
            where session_id is null
            order by format, match_key, enqueued_at, player_id
            limit @batch
            """, new { batch = SweepBatch }, cancellationToken), readOnly: true, cancellationToken: cancellationToken);

        foreach (var group in waiting.GroupBy(entry => (entry.Format, entry.MatchKey)))
        {
            var queue = group.ToArray();
            for (var index = 0; index + 1 < queue.Length;)
            {
                var paired = await TryPairAsync(tenantId, group.Key.Format, group.Key.MatchKey, queue[index].PlayerId, queue[index + 1].PlayerId, cancellationToken);
                index += paired ? 2 : 1;
            }
        }
    }

    private async Task<bool> TryPairAsync(
        Guid tenantId, string format, string matchKey, Guid first, Guid second, CancellationToken cancellationToken)
    {
        try
        {
            return await database.InTenantAsync(tenantId, async transaction =>
            {
                Guid[] ids = [first, second];
                await locks.PlayersAsync(transaction, ids, cancellationToken);

                var parameters = new { format, matchKey, ids };
                var entries = (await transaction.QueryAsync<Entry>("""
                    select player_id as PlayerId, settings::text as Settings, enqueued_at as EnqueuedAt
                    from matchmaking_entries
                    where format = @format and match_key = @matchKey and session_id is null and player_id = any(@ids)
                    """, parameters, cancellationToken)).OrderBy(entry => entry.EnqueuedAt).ThenBy(entry => entry.PlayerId).ToArray();
                if (entries.Length != 2) return false;

                var deleted = await transaction.ExecuteAsync("""
                    delete from matchmaking_entries
                    where format = @format and match_key = @matchKey and session_id is null and player_id = any(@ids)
                    """, parameters, cancellationToken);
                if (deleted != 2) throw new PairingAbortedException();

                // The first waiter's categories and levels are used.
                var waiter = JsonSerializer.Deserialize(entries[0].Settings, MatchmakingJsonContext.Default.LiveQueueSettings)
                    ?? throw new PairingAbortedException();
                var parametersJson = JsonSerializer.SerializeToElement(
                    new BankParameters(NullIfEmpty(waiter.Categories), NullIfEmpty(waiter.Levels)),
                    MatchmakingJsonContext.Default.BankParameters);
                var request = new SessionCreateRequest(
                    Guid.NewGuid(),
                    format,
                    new SessionContent("bank", parametersJson, waiter.QuestionCount),
                    EmptyRules,
                    Capacity: 2,
                    SessionShape.Started,
                    [new SessionParticipant(entries[0].PlayerId), new SessionParticipant(entries[1].PlayerId)]);
                var created = await sessions.CreateAsync(transaction, entries[0].PlayerId, request, cancellationToken);
                if (created.Status != SessionCreateStatus.Created) throw new PairingAbortedException();

                inbox.Changed(transaction, first);
                inbox.Changed(transaction, second);
                return true;
            }, cancellationToken: cancellationToken);
        }
        catch (PairingAbortedException)
        {
            return false;
        }
    }

    private static IReadOnlyList<T>? NullIfEmpty<T>(IReadOnlyList<T> values) => values.Count == 0 ? null : values;

    private sealed record Candidate(Guid OpponentId, string MatchKey);

    private sealed record Waiting(Guid PlayerId, string Format, string MatchKey);

    private sealed record Entry(Guid PlayerId, string Settings, DateTimeOffset EnqueuedAt);

    private sealed class PairingAbortedException : Exception;
}
