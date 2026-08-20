using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public static class Shared
{
    public static readonly MovableClock Clock = new();
    public static readonly FakeQuestions Questions = new();
    public static readonly FakeArchive Archive = new();
    public static readonly FakeLeaderboard Leaderboard = new();
    public static readonly FakePlayers Players = new();
}
