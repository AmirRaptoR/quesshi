namespace Quesshi.Server.Tests;

/// <summary>
/// Match ids, share codes and guest player ids in <see cref="LiveClusterCollection"/> all land in the
/// one shared <see cref="LiveShared.Archive"/>/<see cref="LiveShared.Players"/>, and <see cref="FakeIdFactory"/>
/// mints them as <c>$"id-{n}"</c>/<c>$"CODE{n}"</c> from a plain integer counter — two factories that
/// mint the same <c>n</c> produce the exact same string and collide (issue #34: a colliding match id
/// handed <c>LiveEndpointsTests</c> a stale, already-expired lobby another test class had created).
/// Every minting site in the collection reserves its own disjoint slice of the integer line here, the
/// same convention <see cref="LiveApiTestHost"/>/<see cref="AdminApiTestHost"/> already use for their
/// own hosts via their <c>_idSeed</c> fields — <see cref="LiveIdRangesTests"/> proves the slices below
/// stay disjoint from each other and clear of that convention's zone.
/// </summary>
public static class LiveIdRanges
{
    /// <summary>Reserved for <see cref="LiveShared.Ids"/> — the one factory registered into the silo's
    /// DI container, resolved by every grain in the collection (today only
    /// <c>LiveLobbyGrain.TryCreateDuelAsync</c>, matching two queued players into a duel). One instance
    /// for the whole collection's lifetime, so this must stay wide enough for every match the random
    /// queue ever forms across every test in <see cref="LiveLobbyGrainTests"/>.</summary>
    public const int SharedIdsStart = 0;

    public const int SharedIdsWidth = 9_000;

    /// <summary>The fixed seed <see cref="LiveEndpointsTests.Create_retries_a_colliding_code_and_succeeds_on_a_fresh_one"/>
    /// deliberately collides with itself via <see cref="FakeIdFactory.CodesToRepeat"/> — a fixed point
    /// of its own, not part of the general per-test pool below, so it gets its own reservation.</summary>
    public const int CollidingCodeRetrySeed = 9_001;

    /// <summary>The equivalent fixed seed for
    /// <see cref="LiveEndpointsTests.Create_gives_up_after_repeated_collisions_and_returns_503"/>.</summary>
    public const int CollidingCodeGiveUpSeed = 9_100;

    /// <summary>Headroom reserved around each fixed seed above. Both factories only ever touch a
    /// handful of integers from their seed upward (see their own doc comments) — this is generous, not
    /// a tight bound.</summary>
    public const int FixedSeedWidth = 20;

    /// <summary>Reserved for <see cref="LiveEndpointsTests.NewIds"/>'s per-test pool: each call claims
    /// its own <see cref="NewIdsStep"/>-wide slice starting here, clear of the ranges above and of
    /// <see cref="LiveApiTestHost"/>'s zone (which starts at 100,000).</summary>
    public const int NewIdsPoolStart = 20_000;

    public const int NewIdsStep = 200;

    /// <summary>Headroom for 350 <see cref="LiveEndpointsTests.NewIds"/> calls before the pool would
    /// reach <see cref="LiveApiTestHost"/>'s zone.</summary>
    public const int NewIdsPoolWidth = 70_000;
}
