namespace Quesshi.Shared;

/// <summary>
/// The question as it goes out at round start. Never carries the correct index — that is the one
/// redaction live keeps, the same discipline as an async match's <c>QuestionCardDto</c>.
/// </summary>
public sealed record LiveRoundCardDto(int Slot, int TotalRounds, string QuestionId, string Prompt, List<string> Choices,
    string CategoryId, string CategoryName, string CategoryIcon, string CategoryColor, int Level, MediaDto? Media,
    DateTimeOffset StartedAt, DateTimeOffset EndsAt);
