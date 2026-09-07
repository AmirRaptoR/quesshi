namespace Quesshi.Application.Ports;

/// <summary>
/// The outbound port a live duel pushes state through. Lives beside <see cref="IMatchArchive"/> and
/// <see cref="ILeaderboard"/>: the grain depends on this interface only, so it never has to know how
/// — or whether — the two players are actually listening. The transport that implements it (SignalR)
/// is a later sub-issue; this one ships with no production implementation.
/// </summary>
public interface ILiveNotifier
{
    Task CountdownStartedAsync(string matchId, LiveCountdown countdown, CancellationToken ct = default);
    Task RoundStartedAsync(string matchId, LiveRoundCard card, CancellationToken ct = default);
    Task RoundRevealedAsync(string matchId, LiveRoundReveal reveal, CancellationToken ct = default);
    Task EndedAsync(string matchId, LiveEnded ended, CancellationToken ct = default);

    /// <summary>Declared for the transport sub-issue to call; the grain never invokes this — presence is out of scope here.</summary>
    Task OpponentPresenceChangedAsync(string matchId, string playerId, bool online, CancellationToken ct = default);
}
