namespace Quesshi.Domain;

public sealed record LiveRoundSnapshot(int Slot, string QuestionId, DateTimeOffset StartedAt,
    Dictionary<string, LiveAnswer> Answers);
