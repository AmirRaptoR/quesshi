namespace Quesshi.Server.Tests;

/// <summary>
/// Proves the reservations documented in <see cref="LiveIdRanges"/> hold: no two of them can mint the
/// same integer, and none reaches into <see cref="LiveApiTestHost"/>/<see cref="AdminApiTestHost"/>'s
/// own zone. If someone widens one of these ranges into another's, or forgets to give a new minting
/// site its own entry here, this fails.
/// </summary>
public class LiveIdRangesTests
{
    private readonly record struct Reservation(string Name, int Start, int Width)
    {
        public int End => Start + Width; // exclusive
        public bool Overlaps(Reservation other) => Start < other.End && other.Start < End;
    }

    /// <summary>Every minting site in <see cref="LiveClusterCollection"/> that draws from
    /// <see cref="LiveIdRanges"/>. A new site must add its own entry here.</summary>
    private static readonly Reservation[] Reserved =
    [
        new("LiveShared.Ids", LiveIdRanges.SharedIdsStart, LiveIdRanges.SharedIdsWidth),
        new("Create_retries_a_colliding_code_and_succeeds_on_a_fresh_one (fixed seed)",
            LiveIdRanges.CollidingCodeRetrySeed, LiveIdRanges.FixedSeedWidth),
        new("Create_gives_up_after_repeated_collisions_and_returns_503 (fixed seed)",
            LiveIdRanges.CollidingCodeGiveUpSeed, LiveIdRanges.FixedSeedWidth),
        new("LiveEndpointsTests.NewIds() pool", LiveIdRanges.NewIdsPoolStart, LiveIdRanges.NewIdsPoolWidth),
        new("LiveLobbyEndpointsTests.NewIds() pool", LiveIdRanges.LobbyEndpointsPoolStart, LiveIdRanges.LobbyEndpointsPoolWidth),
    ];

    /// <summary>LiveApiTestHost/AdminApiTestHost mint from their own disjoint 100,000-wide chunks
    /// starting at 100,000 and 10,000,000 respectively (see their <c>_idSeed</c> fields) — everything
    /// reserved above must stay below that floor.</summary>
    private const int ApiTestHostZoneStart = 100_000;

    [Fact]
    public void Every_reserved_range_is_disjoint_from_every_other()
    {
        for (var i = 0; i < Reserved.Length; i++)
            for (var j = i + 1; j < Reserved.Length; j++)
                Assert.False(Reserved[i].Overlaps(Reserved[j]),
                    $"{Reserved[i].Name} [{Reserved[i].Start},{Reserved[i].End}) overlaps " +
                    $"{Reserved[j].Name} [{Reserved[j].Start},{Reserved[j].End})");
    }

    [Fact]
    public void Every_reserved_range_stays_clear_of_the_api_test_host_zone()
    {
        Assert.All(Reserved, r => Assert.True(r.End <= ApiTestHostZoneStart,
            $"{r.Name} [{r.Start},{r.End}) reaches into the LiveApiTestHost/AdminApiTestHost zone"));
    }
}
