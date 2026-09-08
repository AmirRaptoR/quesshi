namespace Quesshi.Domain;

/// <summary>A whole match reduced to plain data, so storage never has to know about the rules.
/// <see cref="Participants"/> replaces the old challenger/opponent pair — <c>Participants[0]</c> is
/// always the owner — mirroring <see cref="LiveMatchSnapshot"/>'s shape wherever the two aggregates
/// genuinely agree. <see cref="Standings"/> carries the ranked result once the match is over; unlike
/// <see cref="LiveMatch"/> there is no abandoners list, since async has no per-round abandonment to
/// record.
///
/// <see cref="Lang"/>, <see cref="ChallengerId"/> and <see cref="OpponentId"/> are what every match
/// ever written before this type grew <see cref="Participants"/>/<see cref="Settings"/> actually looks
/// like on disk. This is grain state in Redis: nothing calls <c>ClearStateAsync</c>, so a finished
/// match's JSON sits there indefinitely, and the async history listing reactivates exactly those
/// grains (<c>GameEndpoints.cs</c>) — a blob written years ago, before this record had these three
/// fields at all, still has to deserialize into something real. It does: <see cref="System.Text.Json"/>
/// leaves an absent constructor argument at its default, so an old blob comes back with
/// <see cref="Participants"/> null (or empty) and these three populated instead, which is exactly the
/// signal <see cref="Match.FromSnapshot"/> keys off. New writes never populate these three —
/// <see cref="Match.ToSnapshot"/> only ever emits the new shape — so their presence on a deserialized
/// record is itself proof the record predates this migration.
/// </summary>
public sealed record MatchSnapshot(
    string Id, string Code, List<string> Participants, int Capacity, DuelSettings Settings,
    List<string> QuestionIds, MatchState State, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    string? WinnerId, bool IsDraw, Dictionary<string, RunSnapshot> Runs, List<Standing> Standings,
    Language? Lang = null, string? ChallengerId = null, string? OpponentId = null);
