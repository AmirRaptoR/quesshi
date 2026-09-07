using Quesshi.Application.Ports;
using StackExchange.Redis;

namespace Quesshi.Infrastructure.Redis;

/// <summary>
/// One key per player, carrying the TTL itself — a silo restart or a dropped connection self-heals
/// with no cleanup job, sweep or reminder anywhere. Ships without a test, like every other Redis
/// adapter in this repo (<see cref="RedisLeaderboard"/>, <see cref="RedisOtpStore"/>,
/// <see cref="RedisResetTokenStore"/>): <c>dotnet test</c> runs with no containers, so there is no
/// Redis-backed test host. The behaviour that matters — the online/offline lifecycle, the guest
/// refusal, the single-bulk-read shape — is covered above this port, against a fake.
/// </summary>
public sealed class RedisPresence(IConnectionMultiplexer redis) : IPresence
{
    private IDatabase Db => redis.GetDatabase();

    private static string Key(string playerId) => $"quesshi:presence:{playerId}";

    public Task MarkOnlineAsync(string playerId, TimeSpan ttl, CancellationToken ct = default)
        => Db.StringSetAsync(Key(playerId), "1", ttl);

    public Task MarkOfflineAsync(string playerId, CancellationToken ct = default)
        => Db.KeyDeleteAsync(Key(playerId));

    /// <summary>
    /// A batch, not a loop of awaited per-key calls — the same "one call, many members" shape
    /// <see cref="RedisLeaderboard.AmongAsync"/> gets from <c>SortedSetScoresAsync</c>. An empty id
    /// list short-circuits without touching Redis at all, as <c>AmongAsync</c> does.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> OnlineAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default)
    {
        if (playerIds.Count == 0) return [];

        var batch = Db.CreateBatch();
        var checks = playerIds.Select(id => (id, exists: batch.KeyExistsAsync(Key(id)))).ToList();
        batch.Execute();
        await Task.WhenAll(checks.Select(c => c.exists));

        return [.. checks.Where(c => c.exists.Result).Select(c => c.id)];
    }
}
