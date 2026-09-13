using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

public sealed class FakeMatchingNotifier : IMatchingNotifier
{
    public sealed record Event(string Kind, string MatchId, object? Payload);
    private readonly List<Event> _events = [];
    private readonly Lock _lock = new();
    public bool ThrowOnEveryCall { get; set; }
    public IReadOnlyList<Event> EventsFor(string id)
    {
        lock (_lock) return [.. _events.Where(e => e.MatchId == id)];
    }
    public Task SlotClosedAsync(string matchId, MatchingSlotClosedPush push, CancellationToken ct = default)
        => Record("SlotClosed", matchId, push);
    public Task RosterChangedAsync(string matchId, CancellationToken ct = default)
        => Record("RosterChanged", matchId, null);
    private Task Record(string kind, string id, object? payload)
    {
        lock (_lock) _events.Add(new Event(kind, id, payload));
        if (ThrowOnEveryCall) throw new InvalidOperationException("Fake notifier failure.");
        return Task.CompletedTask;
    }
}
