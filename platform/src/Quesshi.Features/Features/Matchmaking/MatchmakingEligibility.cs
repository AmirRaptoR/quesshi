using Quesshi.Plugin.Api;

namespace Quesshi.Features.Features.Matchmaking;

/// <summary>
/// The one place that decides whether a waiting entry may still be paired, from its format's match rule:
/// an entry that expired or whose player is banned or deleted is dropped, and so is one whose player
/// must be online and is not. Time is the database's, the same clock that stamps <c>enqueued_at</c>.
/// Callers hold the entries' player locks.
/// </summary>
internal static class MatchmakingEligibility
{
    public sealed record WaitingEntry(Guid PlayerId, string Settings, DateTimeOffset EnqueuedAt, bool Eligible);

    /// <summary>Reads the still-waiting entries of the given players and marks which may be paired.</summary>
    public static async Task<IReadOnlyList<WaitingEntry>> ReadAsync(
        ITenantTx transaction, MatchPolicy rule, string format, string matchKey, Guid[] playerIds, CancellationToken cancellationToken)
    {
        var rows = await transaction.QueryAsync<Row>($"""
            select entry.player_id as PlayerId, entry.settings::text as Settings, entry.enqueued_at as EnqueuedAt,
                   {ExpiredSql} as Expired,
                   (player.banned_at is not null or player.deleted_at is not null) as Unavailable,
                   coalesce(player.last_seen_at < now() - make_interval(secs => @onlineSeconds), true) as Offline
            from matchmaking_entries entry
            join players player on player.tenant_id = entry.tenant_id and player.id = entry.player_id
            where entry.format = @format and entry.match_key = @matchKey and entry.session_id is null
              and entry.player_id = any(@playerIds)
            """, new { format, matchKey, playerIds, lifetimeSeconds = rule.Lifetime?.TotalSeconds, onlineSeconds = rule.OnlineWindow.TotalSeconds }, cancellationToken);
        return rows
            .Select(row => new WaitingEntry(row.PlayerId, row.Settings, row.EnqueuedAt,
                !row.Expired && !row.Unavailable && !(rule.RequireOnline && row.Offline)))
            .ToArray();
    }

    /// <summary>Deletes the given players' waiting entries of a format.</summary>
    public static Task<int> DeleteAsync(ITenantTx transaction, string format, IEnumerable<Guid> playerIds, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("""
            delete from matchmaking_entries where format = @format and session_id is null and player_id = any(@playerIds)
            """, new { format, playerIds = playerIds.ToArray() }, cancellationToken);

    /// <summary>Players whose entry for this format has expired (read without locks; re-checked under the lock).</summary>
    public static async Task<IReadOnlyList<Guid>> ExpiredAsync(
        ITenantTx transaction, MatchPolicy rule, string format, int limit, CancellationToken cancellationToken) =>
        rule.Lifetime is null ? [] : await transaction.QueryAsync<Guid>($"""
            select entry.player_id
            from matchmaking_entries entry
            where entry.format = @format and entry.session_id is null and {ExpiredSql}
            order by entry.enqueued_at
            limit @limit
            """, new { format, limit, lifetimeSeconds = rule.Lifetime?.TotalSeconds }, cancellationToken);

    /// <summary>Deletes the player's entry if it has expired, under that player's lock. True when one was removed.</summary>
    public static async Task<bool> RemoveIfExpiredAsync(
        ITenantTx transaction, MatchPolicy rule, string format, Guid playerId, CancellationToken cancellationToken) =>
        rule.Lifetime is not null && await transaction.ExecuteAsync($"""
            delete from matchmaking_entries entry
            where entry.format = @format and entry.session_id is null and entry.player_id = @playerId and {ExpiredSql}
            """, new { format, playerId, lifetimeSeconds = rule.Lifetime?.TotalSeconds }, cancellationToken) > 0;

    private const string ExpiredSql =
        "(@lifetimeSeconds::double precision is not null and entry.enqueued_at + make_interval(secs => @lifetimeSeconds::double precision) <= now())";

    private sealed record Row(Guid PlayerId, string Settings, DateTimeOffset EnqueuedAt, bool Expired, bool Unavailable, bool Offline);
}
