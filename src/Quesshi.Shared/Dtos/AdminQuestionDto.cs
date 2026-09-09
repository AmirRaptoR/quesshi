namespace Quesshi.Shared;

/// <summary>
/// A question as the admin panel reads it back. <see cref="Kind"/>, <see cref="Target"/> and
/// <see cref="BaseLayer"/> come last and default, so nothing that built one of these before the new
/// kinds existed has to change — and a listing row can now say what shape a question is without
/// having to guess it from an empty choices list.
/// </summary>
public sealed record AdminQuestionDto(string Id, string Lang, string CategoryId, int Level, string Prompt,
    List<string> Choices, int CorrectIndex, string? Explanation, string Status, string Source,
    MediaDto? Media, DateTimeOffset CreatedAt, int TimesServed, int TimesCorrect,
    int ReportCount, List<QuestionReportDto> Reports,
    string Kind = "choice", MapTargetDto? Target = null, string? BaseLayer = null);
