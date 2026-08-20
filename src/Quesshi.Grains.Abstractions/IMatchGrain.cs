namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.IMatchGrain")]
public interface IMatchGrain : IGrainWithStringKey
{
    [Alias("CreateAsync")]
    Task<MatchView> CreateAsync(int lang, string challengerId, List<string> questionIds, string code);
    [Alias("JoinAsync")]
    Task<bool> JoinAsync(string playerId);
    [Alias("ServeNextAsync")]
    Task<ServedSlot?> ServeNextAsync(string playerId);
    [Alias("AnswerAsync")]
    Task<AnswerOutcome> AnswerAsync(string playerId, int slot, int choiceIndex);

    /// <summary>Redacted for the asking player: the opponent's choices appear only once you have finished your own run.</summary>
    [Alias("GetAsync")]
    Task<MatchView?> GetAsync(string forPlayerId);
}
