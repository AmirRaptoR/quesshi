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
    /// Fired once, the instant <c>LiveMatch.CloseRound</c> adds a player to <c>Abandoners</c> — the
    /// wire counterpart of an elimination the round DTOs alone cannot express. Delivered to the whole
    /// duel group, not just the eliminated player: their own client is what switches to spectating,
    /// and everyone else's is what stops waiting on a seat that will never fill again.
    /// </summary>
    Task PlayerEliminatedAsync(string matchId, LivePlayerEliminated elimination, CancellationToken ct = default);

    /// <summary>
    /// A rematch lobby exists for this finished duel — pushed to the finished duel's own group every
    /// time <c>RequestRematchAsync</c> succeeds, whether this call is the one that created the lobby or
    /// a later one that landed on the same derived id. There is no separate "somebody wants a rematch"
    /// push any more: every other participant learns about it as an ordinary invitation, through
    /// <c>ILobbyNotifier.ChallengeReceivedAsync</c>, not through this duel's own group — except a
    /// guest, who can never receive that in-app invitation (see <c>LobbyHub.OnConnectedAsync</c>'s own
    /// remarks) and is therefore reached only through this push: <paramref name="newMatchCode"/> is
    /// the share code that a guest already connected to this duel's own group can use to join the
    /// rematch lobby by link, with no in-app delivery attempted for them at all.
    /// </summary>
    Task RematchCreatedAsync(string matchId, string newMatchId, string newMatchCode, CancellationToken ct = default);

    /// <summary>The lobby could not be created — the live kill switch was off.</summary>
    Task RematchFailedAsync(string matchId, CancellationToken ct = default);
}
