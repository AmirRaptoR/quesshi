namespace Quesshi.Domain;

/// <summary>A whole live duel reduced to plain data, so storage never has to know about the rules.
/// <see cref="Participants"/> replaces the old challenger/opponent pair — <c>Participants[0]</c> is
/// always the owner — and <see cref="Abandoners"/>/<see cref="Standings"/> carry what
/// <see cref="LiveMatch"/> otherwise cannot rebuild from the rounds alone (an abandoner's round slot,
/// and a placement that depends on the abandoners set as much as the scoreboard).
///
/// <see cref="Lang"/>, <see cref="ChallengerId"/>, <see cref="OpponentId"/> and <see cref="AbandonedBy"/>
/// are what every live duel ever written before this type grew <see cref="Participants"/>/
/// <see cref="Settings"/>/<see cref="Abandoners"/> actually looks like on disk — see the equivalent
/// remarks on <see cref="MatchSnapshot"/> for why a blob that old can still surface today and has to
/// deserialize into something real. <see cref="LiveMatch.FromSnapshot"/> keys off an empty
/// <see cref="Participants"/> the same way, and additionally folds a non-null <see cref="AbandonedBy"/>
/// into a single-entry <see cref="Abandoners"/> list — the two-player shape could only ever record one
/// quitter, so the round slot it lost track of never mattered for ranking. New writes never populate
/// these four; <see cref="LiveMatch.ToSnapshot"/> only ever emits the new shape.
/// </summary>
public sealed record LiveMatchSnapshot(
    string Id, string Code, List<string> Participants, int Capacity, DuelSettings Settings,
    List<string> QuestionIds, MatchState State, LivePhase Phase, DateTimeOffset? PhaseEndsAt,
    List<LiveRoundSnapshot> Rounds, Dictionary<string, int> MissStreaks, DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt, string? WinnerId, bool IsDraw, List<Abandonment> Abandoners,
    List<Standing> Standings, NoContestReason? Reason,
    Language? Lang = null, string? ChallengerId = null, string? OpponentId = null, string? AbandonedBy = null);
