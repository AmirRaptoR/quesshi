namespace Quesshi.Domain;

/// <summary>
/// The outcome of one player's rematch press, carried across the grain boundary as an <c>int</c> the
/// same way <see cref="LiveJoinResult"/> is.
/// </summary>
public enum RematchStatus
{
    /// <summary>Not a participant, the duel is not over, or it never got an opponent.</summary>
    Refused,

    /// <summary>The other side has not (yet, or any longer) pressed too.</summary>
    Waiting,

    /// <summary>Both sides were ready; a fresh duel now exists and both players are joined.</summary>
    Created,

    /// <summary>Both sides were ready, but the fresh duel could not be built — the kill switch is
    /// off, or there were not enough questions. Readiness is cleared on both sides either way.</summary>
    Failed
}
