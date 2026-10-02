namespace Quesshi.Domain;

/// <summary>Storage shape for one served voting slot, including its private answer map.</summary>
public sealed record VotingSlotSnapshot(
    int Slot,
    string QuestionId,
    string Prompt,
    List<VotingServedOption> Options,
    DateTimeOffset ServedAt,
    Dictionary<string, VotingAnswer> Answers,
    MediaRef? Media = null);
