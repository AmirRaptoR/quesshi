namespace Quesshi.Shared;

/// <summary>What a player sees while answering. Deliberately carries no correct answer.</summary>
public sealed record QuestionCardDto(int Slot, string QuestionId, string Prompt, List<string> Choices,
    string CategoryId, string CategoryName, string CategoryIcon, string CategoryColor, int Level, MediaDto? Media,
    int SecondsLimit, int TotalSlots);
