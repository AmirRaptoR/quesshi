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

    /// <summary>
    /// Fired once, on the first answer of a round only — the second answer closes the round and
    /// <see cref="RoundRevealedAsync"/> supersedes it. Carries the match, the round slot and who
    /// just answered, and nothing else: no choice index, no score. It is the only way a client can
    /// show "they have answered" without polling — neither <see cref="RoundStartedAsync"/> nor
    /// <see cref="RoundRevealedAsync"/> fires when only one side is in.
    /// </summary>
    Task OpponentAnsweredAsync(string matchId, int slot, string playerId, CancellationToken ct = default);

    /// <summary>
    /// The first rematch press: without this the second press has no prompt, and in practice the
    /// handshake rarely completes. Fired once, to the finished duel's own group.
    /// </summary>
    Task RematchRequestedAsync(string matchId, string playerId, CancellationToken ct = default);

    /// <summary>The second press: a fresh duel now exists. Pushed to the finished duel's group so both clients navigate to it.</summary>
    Task RematchCreatedAsync(string matchId, string newMatchId, CancellationToken ct = default);

    /// <summary>Both sides were ready but the fresh duel could not be built — the kill switch was off, or there were not enough questions.</summary>
    Task RematchFailedAsync(string matchId, CancellationToken ct = default);
}
