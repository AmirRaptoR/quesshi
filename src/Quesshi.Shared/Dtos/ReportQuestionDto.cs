namespace Quesshi.Shared;

/// <summary>Reason is one of: wronganswer, unclear, duplicate, offensive, other.</summary>
public sealed record ReportQuestionDto(string QuestionId, string Reason);
