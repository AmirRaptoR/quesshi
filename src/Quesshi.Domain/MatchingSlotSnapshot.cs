namespace Quesshi.Domain;

/// <summary>Storage shape for one served matching slot, including its private answer map.</summary>
public sealed record MatchingSlotSnapshot(
    int Slot,
    string QuestionId,
    string Prompt,
    List<MatchingServedOption> Options,
    DateTimeOffset ServedAt,
    Dictionary<string, MatchingAnswer> Answers,
    MediaRef? Media = null);
