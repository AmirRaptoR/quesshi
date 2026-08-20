namespace Quesshi.Domain;

public sealed record QuestionReport(string PlayerId, ReportReason Reason, DateTimeOffset At);
