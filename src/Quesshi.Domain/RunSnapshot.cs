namespace Quesshi.Domain;

public sealed record RunSnapshot(List<AnswerRecord> Answers, DateTimeOffset? ServedAt);
