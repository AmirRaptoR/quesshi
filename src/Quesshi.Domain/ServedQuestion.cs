namespace Quesshi.Domain;

public sealed record ServedQuestion(int Index, string QuestionId, DateTimeOffset ServedAt);
