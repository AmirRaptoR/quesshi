using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// Issue #54's whole claim — "the repository has exactly one writer" — proved at the grain, not the
/// HTTP layer: <see cref="IPlayerGrain.UpdateProfileAsync"/> and <see cref="IPlayerGrain.SettleMatchAsync"/>
/// share the one activation's <c>_player</c> cache, so a write from one can no longer be silently
/// undone by a write from the other landing later — the exact bug both endpoints had while they wrote
/// straight to <c>IPlayerRepository</c> behind this grain's back. <see cref="IPlayerGrain.SetBannedAsync"/>
/// gets the same proof for the same reason, against a settlement rather than a profile edit.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class PlayerGrainWriteOwnershipTests(ClusterFixture fixture)
{
    private static string NewPlayerId() => $"p-writes-{Guid.NewGuid():N}";

    private async Task<(IPlayerGrain Grain, string Id)> NewPlayerAsync()
    {
        var id = NewPlayerId();
        await Shared.Players.UpsertAsync(Player.Register(id, $"{id}@example.com", "Amir", Language.En, Shared.Clock.Now));
        return (fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id), id);
    }

    /// <summary>
    /// The actual bug (spec section 4 / issue #54): before this issue, <c>PUT /api/me</c> wrote a
    /// rename straight through <c>IPlayerRepository</c>, while <c>PlayerGrain</c> kept its own, now
    /// stale, in-memory copy of the pre-rename player. The next settled match upserted that stale copy
    /// whole, silently reverting the rename. Routing the rename through the same grain that owns
    /// settlement closes the gap: both mutate the identical cached <c>Player</c>, so neither can undo
    /// the other's write.
    /// </summary>
    [Fact]
    public async Task A_settled_match_does_not_revert_a_rename()
    {
        var (grain, id) = await NewPlayerAsync();

        Assert.True(await grain.UpdateProfileAsync("Renamed Amir", (int)Language.En, null));
        Assert.Equal("Renamed Amir", (await Shared.Players.GetAsync(id))!.DisplayName);

        // Stands in for a duel ending after the rename — the exact ordering the bug needed.
        await grain.SettleMatchAsync("m-after-rename", (int)MatchOutcome.Win, 250, [], [], null);

        var player = (await Shared.Players.GetAsync(id))!;
        Assert.Equal("Renamed Amir", player.DisplayName);
        Assert.Equal(250, player.Stats.TotalScore);
    }

    [Fact]
    public async Task UpdateProfileAsync_also_applies_language_and_a_valid_avatar_seed()
    {
        var (grain, id) = await NewPlayerAsync();

        Assert.True(await grain.UpdateProfileAsync("Amir Two", (int)Language.Nl, "avatar-7"));

        var player = (await Shared.Players.GetAsync(id))!;
        Assert.Equal("Amir Two", player.DisplayName);
        Assert.Equal(Language.Nl, player.Lang);
        Assert.Equal("avatar-7", player.AvatarSeed);
    }

    /// <summary>A null avatar seed is "leave it alone", not "clear it" — <c>UpdateProfileDto</c>'s own
    /// documented convention, carried through the grain rather than reset to something new.</summary>
    [Fact]
    public async Task UpdateProfileAsync_leaves_the_avatar_untouched_when_the_seed_is_null()
    {
        var (grain, id) = await NewPlayerAsync();
        await grain.UpdateProfileAsync("First Name", (int)Language.En, "avatar-3");

        await grain.UpdateProfileAsync("Second Name", (int)Language.En, null);

        var player = (await Shared.Players.GetAsync(id))!;
        Assert.Equal("Second Name", player.DisplayName);
        Assert.Equal("avatar-3", player.AvatarSeed);
    }

    [Fact]
    public async Task UpdateProfileAsync_is_a_no_op_for_an_unknown_player()
        => Assert.False(await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(NewPlayerId())
            .UpdateProfileAsync("Nobody", (int)Language.En, null));

    /// <summary>
    /// The sharper twin of the rename bug: before this issue, <c>POST /admin/users/{id}/ban</c> wrote
    /// straight through <c>IPlayerRepository</c> too, so a match settling for the banned player right
    /// afterwards would upsert <c>PlayerGrain</c>'s stale, still-unbanned copy and silently lift the
    /// ban. Both now go through the one grain activation, so the settlement's write carries the ban
    /// forward instead of overwriting it.
    /// </summary>
    [Fact]
    public async Task A_grain_write_does_not_undo_a_ban()
    {
        var (grain, id) = await NewPlayerAsync();

        Assert.True(await grain.SetBannedAsync(true));
        Assert.True((await Shared.Players.GetAsync(id))!.IsBanned);

        // Stands in for a duel settling after the ban — the exact ordering the bug needed.
        await grain.SettleMatchAsync("m-after-ban", (int)MatchOutcome.Loss, 10, [], [], null);

        Assert.True((await Shared.Players.GetAsync(id))!.IsBanned);
    }

    [Fact]
    public async Task SetBannedAsync_is_a_no_op_for_an_unknown_player()
        => Assert.False(await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(NewPlayerId()).SetBannedAsync(true));
}
