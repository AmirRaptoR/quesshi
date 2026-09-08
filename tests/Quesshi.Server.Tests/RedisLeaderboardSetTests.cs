using Quesshi.Infrastructure.Redis;
using StackExchange.Redis;

namespace Quesshi.Server.Tests;

/// <summary>
/// <see cref="RedisLeaderboard.SetAsync"/> against a real Redis, proving it overwrites a member's
/// score outright rather than incrementing it — the property settlement's retry safety depends on.
/// Skips itself when no Redis endpoint is reachable, so a container-free run never fails on it,
/// matching how the rest of the suite treats no-containers.
/// </summary>
public class RedisLeaderboardSetTests
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
    public async Task Setting_a_score_twice_overwrites_rather_than_accumulating()
    {
        await using var redis = await TryConnectAsync();
        if (redis is null) return; // No Redis reachable: this is what a container-free run looks like.

        // Randomised so this test can never collide with a real player, or with another run of
        // itself, on a Redis shared with anything else.
        var id = $"p-redistest-{Guid.NewGuid():N}";
        var db = redis.GetDatabase();
        try
        {
            var board = new RedisLeaderboard(redis);

            await board.SetAsync(id, 500);
            Assert.Equal(500, await db.SortedSetScoreAsync(Key, id));

            // A repeat call with the identical total — exactly what a retried settlement does — is a
            // true no-op; a lower total, as a stale Player.Stats.TotalScore projection would produce
            // an accidental discount, is honoured as the new absolute value, not ignored or summed.
            await board.SetAsync(id, 500);
            Assert.Equal(500, await db.SortedSetScoreAsync(Key, id));

            await board.SetAsync(id, 300);
            Assert.Equal(300, await db.SortedSetScoreAsync(Key, id));
        }
        finally
        {
            await db.SortedSetRemoveAsync(Key, id);
        }
    }
}
