namespace Quesshi.Application.Ports;

/// <summary>
/// The 3-2-1 once the lobby is full enough to start. <see cref="ParticipantIds"/> replaced the
/// two-named <c>ChallengerId</c>/<c>OpponentId</c> pair as part of issue #53's wire-contract widening —
/// nothing currently reads this beyond the no-op <c>SignalRLiveNotifier.CountdownStartedAsync</c> (no
/// client listens for this push; the phase is read from the catch-up view instead, per #13's own
/// contract-additions note), but it is still a two-player-shaped type that would silently mislead
/// anyone who did wire it up for a capacity&gt;2 duel.
/// </summary>
public sealed record LiveCountdown(DateTimeOffset EndsAt, List<string> ParticipantIds, int TotalRounds);
