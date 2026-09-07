namespace Quesshi.Shared;

/// <summary>
/// The whole live duel as one record, redacted for whoever asked. Every deadline is absolute server
/// time, not a remaining-seconds count, so a reconnecting client can redraw the right arc with no
/// per-round sync. <c>ServerNow</c> is a wire-only addition beside <c>PhaseEndsAt</c>: it is what
/// lets a client measure clock skew once at connect and never re-sync.
/// </summary>
public sealed record LiveViewDto(
    string Id, string ChallengerId, string? OpponentId, string State, string Phase,
    DateTimeOffset? PhaseEndsAt, DateTimeOffset ServerNow, int RoundIndex, int TotalRounds,
    List<LivePlayerViewDto> Players, List<LiveRoundResultViewDto> Rounds,
    string? WinnerId, bool IsDraw, string? AbandonedBy, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt);
