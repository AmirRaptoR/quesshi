using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <remarks>
/// <paramref name="IsLive"/> is the one flag that tells the async and live worlds apart in the
/// single <c>Matches</c> collection they share; it defaults to false so every row written before
/// live duels existed still reads as async.
/// </remarks>
public sealed record ArchivedMatch(
    string Id, string Code, Language Lang, string ChallengerId, string? OpponentId, string? WinnerId, bool IsDraw,
    /// <summary>Every participant's row — <see cref="ChallengerId"/> first, then <see cref="OpponentId"/>
    /// when it is not null, mirroring the old positional pair this replaces. Nobody constructs one for
    /// a participant that does not exist: a lobby with a null <see cref="OpponentId"/> gets a one-entry
    /// list, not a phantom second row.</summary>
    List<ParticipantResult> Results,
    MatchState State, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    List<string> QuestionIds,
    /// <summary>
    /// Last and defaulted, so every existing caller keeps compiling and an async duel archived
    /// before live duels existed reads back as what it always was: not one.
    /// </summary>
    bool IsLive = false)
{
    /// <summary>
    /// Compatibility projection of <see cref="Results"/>'s first entry — the shape every consumer of
    /// this row was written against before N participants existed. <c>Mappers.ToLiveSummary</c> is the
    /// one still reading it; issue #56 deletes this once it and any sibling read <see cref="Results"/>
    /// directly.
    /// </summary>
    [Obsolete("Use Results (Results[0].Score, or find by PlayerId). Deleted in issue #56.")]
    public int ChallengerScore => Results.Count > 0 ? Results[0].Score : 0;

    /// <summary>Compatibility projection of <see cref="Results"/>'s second entry. Deleted in issue #56.</summary>
    [Obsolete("Use Results (Results[1].Score, or find by PlayerId). Deleted in issue #56.")]
    public int OpponentScore => Results.Count > 1 ? Results[1].Score : 0;
}
