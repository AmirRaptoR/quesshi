namespace Quesshi.Shared;

/// <summary>
/// One seated player's identity, as the duel bar, countdown screen and results list all need to draw
/// them: who they are, not what they have scored — <see cref="LivePlayerViewDto"/> already carries
/// the running total, so a client is never asked to zip two parallel lists together just to put a
/// name next to a number. <see cref="LiveViewDto.Participants"/> orders these exactly as
/// <c>LiveMatch.Participants</c> does: index 0 is always the lobby's owner, and every later seat
/// follows in join order. This is what replaced the old <c>ChallengerId</c>/<c>OpponentId</c> pair —
/// a capacity-2 duel still reads as "owner, then the one other seat", which is every observable
/// behaviour a 1v1 ever had; a capacity&gt;2 duel simply has more entries.
/// </summary>
public sealed record LiveParticipantDto(string PlayerId, string Name, string Avatar, bool IsGuest);
