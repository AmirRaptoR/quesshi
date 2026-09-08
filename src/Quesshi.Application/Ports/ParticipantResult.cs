using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>
/// One participant's row in an archived match — <see cref="Standing"/>'s shape, carried into permanent
/// storage. <see cref="Place"/> is 0 for a duel that has not finished yet: <see cref="Standing"/>'s own
/// "1 is first" convention leaves 0 free as "not ranked", which every archive write made while a match
/// is still running uses (nothing reads placement before <c>ArchivedMatch.State</c> says the duel is
/// over), and <see cref="Outcome"/> is a harmless <see cref="MatchOutcome.Loss"/> placeholder alongside
/// it for the same reason.
/// </summary>
public sealed record ParticipantResult(string PlayerId, int Score, int Place, MatchOutcome Outcome);
