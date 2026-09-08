namespace Quesshi.Server.Tests;

/// <summary>
/// Own statics for <see cref="MatchRecoveryClusterCollection"/>, exactly per the pattern
/// <see cref="LiveShared"/> already established for the live cluster: a durable-settlement test needs
/// to fail one specific grain's storage write and inspect the result afterwards, and a
/// <see cref="Shared"/>-wide store shared with every other test class in the assembly would make that
/// fragile in the same way <see cref="Shared.Players"/> already is for player ids reused across
/// <see cref="MatchGrainTests"/> methods. <see cref="Storage"/> is the whole reason this collection
/// exists: it is wired into its own silo as the "hot" provider in place of the ordinary in-memory one,
/// so these tests are the only ones that can ever see a failed write or a seeded legacy row.
/// </summary>
public static class MatchRecoveryShared
{
    public static readonly MovableClock Clock = new();
    public static readonly FakeQuestions Questions = new();
    public static readonly FakeArchive Archive = new();
    public static readonly FakeLeaderboard Leaderboard = new();
    public static readonly FakePlayers Players = new();
    public static readonly FaultyMatchStorage Storage = new();
}
