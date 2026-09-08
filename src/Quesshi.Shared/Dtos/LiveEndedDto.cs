namespace Quesshi.Shared;

/// <summary>
/// The wire shape of <c>Quesshi.Application.Ports.LiveEnded</c>. <see cref="Standings"/> is the real
/// per-player ranking; <see cref="WinnerId"/>/<see cref="IsDraw"/> stay the top-of-ranking summary
/// they always were and must never be read as a per-player outcome for more than two players — see
/// the port record's own remarks.
/// </summary>
public sealed record LiveEndedDto(
    string State, string? WinnerId, bool IsDraw, string? AbandonedBy,
    List<LivePlayerScoreDto> Scores, List<StandingDto> Standings, string? Reason);
