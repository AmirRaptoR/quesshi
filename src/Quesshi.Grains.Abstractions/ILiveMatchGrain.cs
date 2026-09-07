namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.ILiveMatchGrain")]
public interface ILiveMatchGrain : IGrainWithStringKey
{
    /// <summary>Idempotent: returns the existing view if the duel already exists.</summary>
    [Alias("CreateAsync")]
    Task<LiveView> CreateAsync(string challengerId, List<string> questionIds);

    /// <summary>False rather than an exception when the challenge is already taken or expired.</summary>
    [Alias("JoinAsync")]
    Task<bool> JoinAsync(string playerId);

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
