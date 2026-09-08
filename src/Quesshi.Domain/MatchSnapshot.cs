namespace Quesshi.Domain;

/// <summary>A whole match reduced to plain data, so storage never has to know about the rules.
/// <see cref="Participants"/> replaces the old challenger/opponent pair — <c>Participants[0]</c> is
/// always the owner — mirroring <see cref="LiveMatchSnapshot"/>'s shape wherever the two aggregates
/// genuinely agree. <see cref="Standings"/> carries the ranked result once the match is over; unlike
/// <see cref="LiveMatch"/> there is no abandoners list, since async has no per-round abandonment to
/// record.</summary>
public sealed record MatchSnapshot(
    string Id, string Code, List<string> Participants, int Capacity, DuelSettings Settings,
    List<string> QuestionIds, MatchState State, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    string? WinnerId, bool IsDraw, Dictionary<string, RunSnapshot> Runs, List<Standing> Standings);
