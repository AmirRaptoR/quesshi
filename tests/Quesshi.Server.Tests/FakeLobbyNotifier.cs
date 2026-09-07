using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

/// <summary>Records every call, the same shape as <see cref="FakeLiveNotifier"/>.</summary>
public sealed class FakeLobbyNotifier : ILobbyNotifier
{
    public sealed record Event(string Kind, string PlayerId, object? Payload);

    private readonly List<Event> _events = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<Event> EventsFor(string playerId)
    {
        lock (_lock) return [.. _events.Where(e => e.PlayerId == playerId)];
    }

    public void Clear()
    {
        lock (_lock) _events.Clear();
    }

    public Task ChallengeReceivedAsync(string targetId, LiveChallengeNotice challenge, CancellationToken ct = default)
        => Record("ChallengeReceived", targetId, challenge);

    public Task ChallengeExpiredAsync(string challengerId, string challengeId, CancellationToken ct = default)
        => Record("ChallengeExpired", challengerId, challengeId);

    public Task ChallengeDeclinedAsync(string challengerId, string challengeId, CancellationToken ct = default)
        => Record("ChallengeDeclined", challengerId, challengeId);

    public Task DuelReadyAsync(string playerId, string matchId, CancellationToken ct = default)
        => Record("DuelReady", playerId, matchId);

    public Task ChallengeFailedAsync(string playerId, string challengeId, CancellationToken ct = default)
        => Record("ChallengeFailed", playerId, challengeId);

    private Task Record(string kind, string playerId, object? payload)
    {
        lock (_lock) _events.Add(new Event(kind, playerId, payload));
        return Task.CompletedTask;
    }
}
