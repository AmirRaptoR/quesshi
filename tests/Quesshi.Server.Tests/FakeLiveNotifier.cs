using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

/// <summary>Records every call, keyed by match id so tests running against different duels in the
/// same shared silo never see each other's events.</summary>
public sealed class FakeLiveNotifier : ILiveNotifier
{
    public sealed record Event(string Kind, string MatchId, object Payload);

    private readonly List<Event> _events = [];
    private readonly Lock _lock = new();

    /// <summary>When set, every call records its event and then throws — for the "the clock keeps running" tests.</summary>
    public bool ThrowOnEveryCall { get; set; }

    public IReadOnlyList<Event> EventsFor(string matchId)
    {
        lock (_lock) return [.. _events.Where(e => e.MatchId == matchId)];
    }

    public Task CountdownStartedAsync(string matchId, LiveCountdown countdown, CancellationToken ct = default) => Record("CountdownStarted", matchId, countdown);
    public Task RoundStartedAsync(string matchId, LiveRoundCard card, CancellationToken ct = default) => Record("RoundStarted", matchId, card);
    public Task RoundRevealedAsync(string matchId, LiveRoundReveal reveal, CancellationToken ct = default) => Record("RoundRevealed", matchId, reveal);
    public Task EndedAsync(string matchId, LiveEnded ended, CancellationToken ct = default) => Record("Ended", matchId, ended);
    public Task OpponentPresenceChangedAsync(string matchId, string playerId, bool online, CancellationToken ct = default)
        => Record("OpponentPresenceChanged", matchId, (playerId, online));
    public Task OpponentAnsweredAsync(string matchId, int slot, string playerId, CancellationToken ct = default)
        => Record("OpponentAnswered", matchId, (slot, playerId));
    public Task RematchRequestedAsync(string matchId, string playerId, CancellationToken ct = default)
        => Record("RematchRequested", matchId, playerId);
    public Task RematchCreatedAsync(string matchId, string newMatchId, CancellationToken ct = default)
        => Record("RematchCreated", matchId, newMatchId);
    public Task RematchFailedAsync(string matchId, CancellationToken ct = default)
        => Record("RematchFailed", matchId, matchId);

    private Task Record(string kind, string matchId, object payload)
    {
        lock (_lock) _events.Add(new Event(kind, matchId, payload));
        if (ThrowOnEveryCall) throw new InvalidOperationException("Fake notifier failure.");
        return Task.CompletedTask;
    }
}
