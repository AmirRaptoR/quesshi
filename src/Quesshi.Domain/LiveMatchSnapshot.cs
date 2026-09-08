namespace Quesshi.Domain;

/// <summary>A whole live duel reduced to plain data, so storage never has to know about the rules.
/// <see cref="Participants"/> replaces the old challenger/opponent pair — <c>Participants[0]</c> is
/// always the owner — and <see cref="Abandoners"/>/<see cref="Standings"/> carry what
/// <see cref="LiveMatch"/> otherwise cannot rebuild from the rounds alone (an abandoner's round slot,
/// and a placement that depends on the abandoners set as much as the scoreboard).</summary>
public sealed record LiveMatchSnapshot(
    string Id, string Code, List<string> Participants, int Capacity, DuelSettings Settings,
    List<string> QuestionIds, MatchState State, LivePhase Phase, DateTimeOffset? PhaseEndsAt,
    List<LiveRoundSnapshot> Rounds, Dictionary<string, int> MissStreaks, DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt, string? WinnerId, bool IsDraw, List<Abandonment> Abandoners,
    List<Standing> Standings, NoContestReason? Reason);
