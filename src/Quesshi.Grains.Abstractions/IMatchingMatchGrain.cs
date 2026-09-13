namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.IMatchingMatchGrain")]
public interface IMatchingMatchGrain : IGrainWithStringKey
{
    [Alias("CreateAsync")]
    Task<MatchingView> CreateAsync(string code, string ownerId, int lang, int questionCount,
        List<string> categoryIds, int capacity);

    [Alias("JoinAsync")]
    Task<int> JoinAsync(string playerId);

    [Alias("StartAsync")]
    Task<bool> StartAsync(string playerId);

    [Alias("LeaveAsync")]
    Task<bool> LeaveAsync(string playerId);

    [Alias("UpdateSettingsAsync")]
    Task<bool> UpdateSettingsAsync(string playerId, int lang, int questionCount, List<string> categoryIds,
        List<int> levels, int? capacity, int mode);

    [Alias("AnswerAsync")]
    Task<MatchingView> AnswerAsync(string playerId, int slot, int kind, string? participantId, int? choiceIndex);

    [Alias("GetAsync")]
    Task<MatchingView?> GetAsync(string playerId);
}
