using Quesshi.Application.Ports;
using StackExchange.Redis;

namespace Quesshi.Infrastructure.Redis;

/// <summary>Global all-time score board. A sorted set is exactly the right shape, so use it as one.</summary>
public sealed class RedisLeaderboard(IConnectionMultiplexer redis) : ILeaderboard
{
    private const string Key = "quesshi:leaderboard";

    private IDatabase Db => redis.GetDatabase();

    public Task AddAsync(string playerId, long delta, CancellationToken ct = default)
        => Db.SortedSetIncrementAsync(Key, playerId, delta);

    public async Task<IReadOnlyList<LeaderboardEntry>> TopAsync(int count, CancellationToken ct = default)
    {
        var entries = await Db.SortedSetRangeByRankWithScoresAsync(Key, 0, count - 1, Order.Descending);
        return [.. entries.Select((e, i) => new LeaderboardEntry(e.Element!, (long)e.Score, i + 1))];
    }

    public async Task<IReadOnlyList<LeaderboardEntry>> AmongAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default)
    {
        if (playerIds.Count == 0) return [];

        var scores = await Db.SortedSetScoresAsync(Key, [.. playerIds.Select(p => (RedisValue)p)]);
        return [.. playerIds.Zip(scores, (id, score) => (id, score: (long)(score ?? 0)))
            .OrderByDescending(x => x.score)
            .Select((x, i) => new LeaderboardEntry(x.id, x.score, i + 1))];
    }
}
