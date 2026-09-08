using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// The abandonment side of <see cref="IPlayerGrain.SettleMatchAsync"/>, exercised through the grain
/// and its persistence, not just the domain method it wraps — proving the escalating schedule
/// survives a real activation and a real player record, the way it will when a live duel actually
/// calls it. Every call here uses a distinct match id (dedup is covered elsewhere) and a null
/// outcome, matching how a quitter's penalty is applied without a result of its own to go with it.
/// A large banked score is seeded up front so the escalating penalty is legible in
/// <c>Stats.TotalScore</c> rather than washed out by the zero floor.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class PlayerGrainAbandonmentTests(ClusterFixture fixture)
{
    private const long Banked = 10_000;

    private static string NewPlayerId() => $"p-abandon-{Guid.NewGuid():N}";

    private async Task<(IPlayerGrain Grain, string Id)> NewGrainAsync()
    {
        var id = NewPlayerId();
        var player = Player.Register(id, $"{id}@example.com", "Amir", Language.En, Shared.Clock.Now);
        player.RecordResult(MatchOutcome.Win, Banked);
        await Shared.Players.UpsertAsync(player);
        return (fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id), id);
    }

    [Fact]
    public async Task The_escalating_penalty_is_charged_onto_the_players_own_record()
    {
        var (grain, id) = await NewGrainAsync();
        var now = Shared.Clock.Now;

        await grain.SettleMatchAsync("m-1", null, 0, [], [], now);
        Assert.Equal(Banked, (await Shared.Players.GetAsync(id))!.Stats.TotalScore); // 1st: free

        await grain.SettleMatchAsync("m-2", null, 0, [], [], now.AddHours(1));
        Assert.Equal(Banked - 200, (await Shared.Players.GetAsync(id))!.Stats.TotalScore); // 2nd: 200

        await grain.SettleMatchAsync("m-3", null, 0, [], [], now.AddHours(2));
        Assert.Equal(Banked - 200 - 400, (await Shared.Players.GetAsync(id))!.Stats.TotalScore); // 3rd: 400
    }

    [Fact]
    public async Task The_window_rolls_across_a_grain_reactivation()
    {
        var (_, id) = await NewGrainAsync();
        var now = Shared.Clock.Now;

        await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id).SettleMatchAsync("m-1", null, 0, [], [], now);

        // Force a fresh activation, so the second call rehydrates the abandonment history from
        // storage rather than reading it out of the same in-memory grain.
        await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id).SettleMatchAsync("m-2", null, 0, [], [], now.AddDays(1));
        Assert.Equal(Banked - 200, (await Shared.Players.GetAsync(id))!.Stats.TotalScore);

        // Seven days after the first call, it has aged out of the window but the second (a day later)
        // has not: this settlement is still the second abandonment standing within its own window,
        // not a fresh first offence, so it costs the same 200 again — not the escalated 400.
        await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id).SettleMatchAsync("m-3", null, 0, [], [], now.AddDays(7));
        Assert.Equal(Banked - 200 - 200, (await Shared.Players.GetAsync(id))!.Stats.TotalScore);
    }
}
