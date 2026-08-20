namespace Quesshi.Shared;

public sealed record RevealedQuestionDto(int Slot, string QuestionId, string Prompt, List<string> Choices, int CorrectIndex,
    int? MyChoice, int? TheirChoice, string CategoryName, string? Explanation, MediaDto? Media);
