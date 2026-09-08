using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// <see cref="IPlayerGrain.SettleMatchAsync"/>'s retry-safety properties: the dedup guard applies a
/// settled match's stats exactly once, the leaderboard projection is written every time regardless of
/// the guard, and a failed write can never leave the in-memory cache believing it succeeded. These are
/// exactly the properties that make it safe for a caller — <c>LiveMatchSettlement</c>, or a match
/// grain's reminder-driven retry — to call this more than once for the same match.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class PlayerGrainSettlementTests(ClusterFixture fixture)
{
    private static string NewPlayerId() => $"p-settle-{Guid.NewGuid():N}";

    private async Task<(IPlayerGrain Grain, string Id)> NewPlayerAsync()
    {
        var id = NewPlayerId();
        await Shared.Players.UpsertAsync(Player.Register(id, $"{id}@example.com", "Amir", Language.En, Shared.Clock.Now));
        return (fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id), id);
    }

    [Fact]
    public async Task Settling_the_same_match_twice_applies_the_stats_exactly_once()
    {
        var (grain, id) = await NewPlayerAsync();

        await grain.SettleMatchAsync("m-dedup", (int)MatchOutcome.Win, 300, ["geography"], [true], null);
        // A repeat for the identical match id — the shape of a retried settlement call — must not
        // add a second win or a second 300 points on top of the first.
        await grain.SettleMatchAsync("m-dedup", (int)MatchOutcome.Win, 300, ["geography"], [true], null);

        var stats = (await Shared.Players.GetAsync(id))!.Stats;
        Assert.Equal(1, stats.Wins);
        Assert.Equal(300, stats.TotalScore);
        Assert.Equal(1.0, (await Shared.Players.GetAsync(id))!.Accuracy("geography"));
    }

    [Fact]
    public async Task A_repeat_call_still_writes_the_leaderboard_even_though_the_stats_are_a_no_op()
    {
        var (grain, id) = await NewPlayerAsync();

        await grain.SettleMatchAsync("m-repair", (int)MatchOutcome.Win, 450, [], [], null);
        Assert.Equal(450, Shared.Leaderboard.Scores[id]);

        // Simulate a Redis write that failed after the first call's Mongo write already committed:
        // the board is left stale relative to the true, already-persisted TotalScore.
        Shared.Leaderboard.Scores[id] = 0;

        // The match is already on the settled list, so this call is a pure no-op for stats — and
        // must still repair the leaderboard, which is the entire point of writing it unconditionally
        // outside the dedup guard rather than only when the guard says a mutation happened.
        await grain.SettleMatchAsync("m-repair", (int)MatchOutcome.Win, 450, [], [], null);

        var stats = (await Shared.Players.GetAsync(id))!.Stats;
        Assert.Equal(1, stats.Wins); // confirms the second call really was a no-op for stats
        Assert.Equal(450, stats.TotalScore);
        Assert.Equal(450, Shared.Leaderboard.Scores[id]); // ...yet the board is repaired anyway
    }

    [Fact]
    public async Task A_failed_write_is_invalidated_so_a_retry_in_the_same_activation_reapplies_the_effect()
    {
        var (grain, id) = await NewPlayerAsync();

        // The very next UpsertAsync for this player throws, standing in for a Mongo write that failed
        // (or whose outcome is unknown) after PlayerGrain had already mutated its in-memory copy.
        Shared.Players.FailNextUpsertFor.Add(id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.SettleMatchAsync("m-retry", (int)MatchOutcome.Win, 700, [], [], null));

        // Nothing was actually persisted: the failed write must not have reached the repository.
        Assert.Equal(0, (await Shared.Players.GetAsync(id))!.Stats.TotalScore);
        Assert.False(Shared.Leaderboard.Scores.ContainsKey(id)); // never reached, since the upsert threw first

        // Retried within the same activation, with the identical match id a real caller would repeat.
        // If the cache had not been invalidated, _player would still be the object mutated (in memory
        // only) by the failed attempt above — which already "remembers" m-retry as settled — and this
        // retry would silently skip the mutation, leaving the score at 0 forever. Invalidation forces
        // a reload from the repository, which never saw the failed write, so the match still looks
        // unsettled and the retry applies it for real.
        await grain.SettleMatchAsync("m-retry", (int)MatchOutcome.Win, 700, [], [], null);

        var stats = (await Shared.Players.GetAsync(id))!.Stats;
        Assert.Equal(1, stats.Wins);
        Assert.Equal(700, stats.TotalScore);
        Assert.Equal(700, Shared.Leaderboard.Scores[id]);
    }

    [Fact]
    public async Task A_guest_is_settled_and_deduped_the_same_way_but_never_reaches_the_leaderboard()
    {
        var guest = Player.Guest($"p-settle-guest-{Guid.NewGuid():N}", "Sara", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(guest);
        var grain = fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(guest.Id);

        await grain.SettleMatchAsync("m-guest", (int)MatchOutcome.Win, 200, [], [], null);
        await grain.SettleMatchAsync("m-guest", (int)MatchOutcome.Win, 200, [], [], null);

        var stats = (await Shared.Players.GetAsync(guest.Id))!.Stats;
        Assert.Equal(1, stats.Wins);
        Assert.Equal(200, stats.TotalScore);
        Assert.False(Shared.Leaderboard.Scores.ContainsKey(guest.Id));
    }
}
