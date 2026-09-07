using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

/// <summary>Records every call, keyed by player id so tests sharing one silo never see each other's pushes.</summary>
public sealed class FakeLobbyNotifier : ILobbyNotifier
{
    public sealed record Event(string Kind, string PlayerId, object? Payload);

    private readonly List<Event> _events = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<Event> EventsFor(string playerId)
    {
        lock (_lock) return [.. _events.Where(e => e.PlayerId == playerId)];
    }

    public Task MatchedAsync(string playerId, string matchId, CancellationToken ct = default) => Record("Matched", playerId, matchId);
    public Task QueueCountChangedAsync(string playerId, int othersWaiting, CancellationToken ct = default) => Record("QueueCount", playerId, othersWaiting);
    public Task QueueFailedAsync(string playerId, CancellationToken ct = default) => Record("QueueFailed", playerId, null);

    private Task Record(string kind, string playerId, object? payload)
    {
        lock (_lock) _events.Add(new Event(kind, playerId, payload));
        return Task.CompletedTask;
    }
}
