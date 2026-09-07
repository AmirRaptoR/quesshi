using Quesshi.Infrastructure.Redis;
using StackExchange.Redis;

namespace Quesshi.Server.Tests;

/// <summary>
/// <see cref="RedisLeaderboard.PenaliseAsync"/> against a real Redis, proving the Lua script itself —
/// not just <see cref="FakeLeaderboard"/>'s in-memory stand-in — floors at zero and never creates a
/// member that was not already on the board. Skips itself when no Redis endpoint is reachable, so a
/// container-free run never fails on it, matching how the rest of the suite treats no-containers.
/// </summary>
public class RedisLeaderboardPenaltyTests
{
    /// <summary>The same "localhost:6379" default <c>Program.cs</c> falls back to with no connection string.</summary>
    private const string Endpoint = "localhost:6379";
    private const string Key = "quesshi:leaderboard";

    private static async Task<IConnectionMultiplexer?> TryConnectAsync()
    {
        try
        {
            var options = ConfigurationOptions.Parse(Endpoint);
            options.ConnectTimeout = 300;
            options.AbortOnConnectFail = true;
            return await ConnectionMultiplexer.ConnectAsync(options);
        }
        catch
        {
            return null;
        }
    }

    [Fact]
    public async Task A_penalty_floors_at_zero_and_never_creates_a_missing_member()
    {
        await using var redis = await TryConnectAsync();
        if (redis is null) return; // No Redis reachable: this is what a container-free run looks like.

        // Randomised so this test can never collide with a real player, or with another run of
        // itself, on a Redis shared with anything else.
        var real = $"p-redistest-{Guid.NewGuid():N}";
        var ghost = $"p-redistest-ghost-{Guid.NewGuid():N}";
        var db = redis.GetDatabase();
        try
        {
            var board = new RedisLeaderboard(redis);

            await board.AddAsync(real, 50);
            await board.PenaliseAsync(real, 200);
            Assert.Equal(0, await db.SortedSetScoreAsync(Key, real));

            await board.PenaliseAsync(ghost, 200);
            Assert.Null(await db.SortedSetScoreAsync(Key, ghost));
        }
        finally
        {
            await db.SortedSetRemoveAsync(Key, [real, ghost]);
        }
    }
}
