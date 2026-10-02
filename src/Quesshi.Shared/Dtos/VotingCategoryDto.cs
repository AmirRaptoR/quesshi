namespace Quesshi.Shared;

/// <summary>Voting's category contract is separate from the trivia category contract and store.</summary>
public sealed record VotingCategoryDto(string Id, string Name, string NameFa, string NameEn, string Icon,
    string Color, bool IsActive, int SortOrder, string NameNl = "");
