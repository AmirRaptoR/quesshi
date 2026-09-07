namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.ILiveMatchGrain")]
public interface ILiveMatchGrain : IGrainWithStringKey
{
    /// <summary>Idempotent: returns the existing view if the duel already exists.</summary>
    [Alias("CreateAsync")]
    Task<LiveView> CreateAsync(string code, int lang, string challengerId, List<string> questionIds);

    /// <summary>
    /// A <see cref="LiveJoinResult"/> carried as <c>int</c>: <see cref="Quesshi.Grains.Abstractions"/>
    /// keeps its Orleans-SDK-only reference list, so the domain enum crosses the boundary as a
    /// number rather than a type this project would have to reference <c>Quesshi.Domain</c> for.
    /// </summary>
    [Alias("JoinAsync")]
    Task<int> JoinAsync(string playerId);

    /// <summary>
    /// True only when the caller is the challenger and the duel is still in the lobby: it ends the
    /// duel as a no-contest and notifies. False leaves the duel running untouched.
    /// </summary>
    [Alias("CancelAsync")]
    Task<bool> CancelAsync(string playerId);

    /// <summary>
    /// True if the answer was recorded, false if it was refused (late, wrong slot, already
    /// answered, not a participant, duel over). No outcome comes back: the opponent is still on the
    /// same question, so returning the correct index here would leak the reveal to whoever answers
    /// first. Both players learn it together, through <c>ILiveNotifier.RoundRevealedAsync</c>.
    /// </summary>
    [Alias("AnswerAsync")]
    Task<bool> AnswerAsync(string playerId, int slot, int choiceIndex);

    /// <summary>Redacted for the asking player: the round in flight never reveals the correct index or the opponent's choice.</summary>
    [Alias("GetAsync")]
    Task<LiveView?> GetAsync(string forPlayerId);

    /// <summary>Admin-facing: finishes an in-flight duel early as a no-contest.</summary>
    [Alias("EndAsync")]
    Task EndAsync(string reason);
}
