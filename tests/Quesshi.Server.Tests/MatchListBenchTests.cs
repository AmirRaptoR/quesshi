using Quesshi.Server.Bench;

namespace Quesshi.Server.Tests;

/// <summary>
/// <see cref="MatchListBench.AssertGrainStateInRedisAsync"/> scans Redis with a wildcard pattern to
/// avoid activating grains, then has to tell an exact id apart from a longer one that merely contains
/// it as a substring — unpadded ids mean "bench-heavy-m1" is a substring of "bench-heavy-m10" through
/// "bench-heavy-m19". These tests cover <see cref="MatchListBench.ContainsExactId"/>, the check that
/// makes that distinction, without needing a real Redis.
/// </summary>
public class MatchListBenchTests
{
    [Theory]
    // The collision the bug allowed: an id that is a prefix of a longer sibling id must not match.
    [InlineData("quesshi/state/match/bench-heavy-m10/match", "bench-heavy-m1", false)]
    [InlineData("quesshi/state/match/bench-heavy-m11/match", "bench-heavy-m1", false)]
    [InlineData("quesshi/state/match/bench-heavy-m19/match", "bench-heavy-m1", false)]
    // The exact id, at the position Orleans' Redis grain storage actually puts it, must match.
    [InlineData("quesshi/state/match/bench-heavy-m1/match", "bench-heavy-m1", true)]
    // An id occurring with a non-alphanumeric boundary on both sides matches wherever it sits.
    [InlineData("bench-heavy-m1", "bench-heavy-m1", true)]
    [InlineData("prefix:bench-heavy-m1", "bench-heavy-m1", true)]
    [InlineData("bench-heavy-m1:suffix", "bench-heavy-m1", true)]
    // No occurrence at all.
    [InlineData("quesshi/state/match/bench-fresh-m1/match", "bench-heavy-m1", false)]
    public void ContainsExactId_distinguishes_an_id_from_a_longer_sibling_id(string key, string id, bool expected) =>
        Assert.Equal(expected, MatchListBench.ContainsExactId(key, id));
}
