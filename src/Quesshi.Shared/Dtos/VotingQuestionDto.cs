namespace Quesshi.Shared;

/// <summary>The authoring view of a voting question. Voting deliberately has no trivia-only
/// fields such as difficulty, a correct answer, explanation or map data.</summary>
public sealed record VotingQuestionDto(string Id, string Lang, string? CategoryId, string Prompt,
    string AnswerSource, List<string> Choices, string Status, string Source, MediaDto? Media,
    string? Topic, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int TimesServed);

public sealed record AdminVotingQuestionPageDto(List<VotingQuestionDto> Items, long Total);
