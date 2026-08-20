namespace Quesshi.Shared;

public sealed record QuestionReportDto(string PlayerId, string PlayerName, string Reason, DateTimeOffset At);
