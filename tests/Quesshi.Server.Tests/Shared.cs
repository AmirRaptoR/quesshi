using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public static class Shared
{
    public static readonly MovableClock Clock = new();
    public static readonly FakeQuestions Questions = new();
    public static readonly FakeCategories Categories = new();
    public static readonly FakeArchive Archive = new();
    public static readonly FakeLeaderboard Leaderboard = new();
    public static readonly FakePlayers Players = new();

    /// <summary>MatchGrain's own notifier — issue #53's lobby push. A separate instance from
    /// LiveShared.Notifier: this assembly's live and async matches run in different silos, and each
    /// silo's fake needs its own event list for the same reason Shared.Players is not LiveShared.Players.</summary>
    public static readonly FakeLiveNotifier Notifier = new();
}
