namespace Quesshi.Shared;

public sealed record AdminQuestionDto(string Id, string Lang, string CategoryId, int Level, string Prompt,
    List<string> Choices, int CorrectIndex, string? Explanation, string Status, string Source,
    MediaDto? Media, DateTimeOffset CreatedAt, int TimesServed, int TimesCorrect,
    int ReportCount, List<QuestionReportDto> Reports);
