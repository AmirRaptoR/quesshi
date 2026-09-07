using Microsoft.Extensions.Time.Testing;

namespace Quesshi.Server.Tests;

public static class LiveShared
{
    public static readonly FakeTimeProvider TimeProvider = new(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
    public static readonly FakeQuestions Questions = new();
    public static readonly FakeCategories Categories = new();
    public static readonly FakeLiveNotifier Notifier = new();
    public static readonly FakeArchive Archive = new();
    public static readonly FakePlayers Players = new();
    public static readonly FakeLobbyNotifier LobbyNotifier = new();
    public static readonly FakeIdFactory Ids = new();
}
