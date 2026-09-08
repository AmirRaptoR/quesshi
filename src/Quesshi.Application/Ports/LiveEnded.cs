using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>
/// Everything <c>ILiveNotifier.EndedAsync</c> pushes once a duel is over. <see cref="Scores"/> has
/// always covered every seat — it was already a <c>List</c> — but <see cref="Standings"/> is the new
/// part: the actual per-player ranking <c>LiveMatch.Standings</c> computes, since "who won" for more
/// than two players is a place and an outcome per player, not a pair of scalars. <see cref="WinnerId"/>
/// and <see cref="IsDraw"/> are kept exactly as <c>LiveMatch</c>'s own properties of the same names
/// mean them — the sole occupant of first place, and whether more than one shares it — and that is
/// all they ever mean here too. A caller that wants a specific player's outcome must read
/// <see cref="Standings"/>, never compare against these two: a three-player finish scoring
/// 100/100/50 is <c>IsDraw = true</c> for the pair sharing first place, not for all three, and the
/// third player did not merely "not draw" — they lost.
/// </summary>
public sealed record LiveEnded(
    MatchState State, string? WinnerId, bool IsDraw, List<string> AbandonedBy,
    List<LivePlayerScore> Scores, List<Standing> Standings, string? Reason);
