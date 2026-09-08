using Microsoft.Extensions.Time.Testing;

namespace Quesshi.Server.Tests;

public static class LiveShared
{
    /// <summary>
    /// One clock for the whole <see cref="LiveClusterCollection"/>: <see cref="LiveTestSilo"/> bakes
    /// this exact instance into the silo's DI container at deploy time, and
    /// <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/> only ever moves forward
    /// (<c>SetUtcNow</c>/<c>Advance</c> both throw on going backward), so no per-class reset is
    /// reachable through the silo. This is safe because every expiry check in the collection compares
    /// elapsed time since a grain's own creation, never an absolute wall-clock value — a lobby created
    /// and checked with no <c>Advance</c> in between is never expired, no matter how far a previous
    /// test class already pushed "now" forward. What is *not* safe on a shared clock is a colliding
    /// match id (see <see cref="LiveIdRanges"/>): that hands one test class a grain another class
    /// already created and advanced past its own expiry.
    /// </summary>
    public static readonly FakeTimeProvider TimeProvider = new(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
    public static readonly FakeQuestions Questions = new();
    public static readonly FakeCategories Categories = new();
    public static readonly FakeLiveNotifier Notifier = new();
    public static readonly FakeLobbyNotifier LobbyNotifier = new();
    public static readonly FakeArchive Archive = new();
    public static readonly FakePlayers Players = new();
    public static readonly FakeLeaderboard Leaderboard = new();
    public static readonly FakeIdFactory Ids = new(LiveIdRanges.SharedIdsStart);
    public static readonly FakeLiveDirectory Directory = new();
}
