namespace Quesshi.Domain;

/// <summary>
/// Why a duel ended with nobody credited. <see cref="MatchState.NoContest"/> alone used to be enough
/// when the only way to reach it was "both players went quiet" — with N players and a widened
/// staleness test, the same state is reachable for reasons that must not be treated alike: only
/// <see cref="AllAbandoned"/> is the players' doing, and only it is eligible for the abandonment
/// penalty. <see cref="Stale"/> and <see cref="LobbyExpired"/> are the server's fault or nobody's at
/// all, and must leave every participant's stats exactly as they found them.
/// </summary>
public enum NoContestReason
{
    /// <summary>Every remaining active participant hit the miss-streak threshold in the same round.
    /// The only reason eligible for the abandonment penalty.</summary>
    AllAbandoned,

    /// <summary>The process was away for longer than <see cref="LiveRules.StaleAfter"/> could excuse
    /// — an outage, not silence from the players. Nobody is penalised for a server that was not
    /// there to see them play.</summary>
    Stale,

    /// <summary>The lobby's own clock ran out before it ever became a duel.</summary>
    LobbyExpired,

    /// <summary>
    /// The owner deliberately ended their own lobby before it ever became a duel — not a clock running
    /// out, so it must not be reported or read as <see cref="LobbyExpired"/>. There is no ownership
    /// transfer: cancelling is the owner's alone, and everyone else seated simply leaves via a normal
    /// join-time seat release instead.
    /// </summary>
    OwnerCancelled
}
