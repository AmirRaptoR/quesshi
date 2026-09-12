namespace Quesshi.Shared;

/// <summary>Matching's category contract is separate from the trivia category contract and store.</summary>
public sealed record MatchingCategoryDto(string Id, string Name, string NameFa, string NameEn, string Icon,
    string Color, bool IsActive, int SortOrder, string NameNl = "");
