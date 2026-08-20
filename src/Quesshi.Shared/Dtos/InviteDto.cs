namespace Quesshi.Shared;

/// <summary>
/// The little that an invite link may reveal before anyone identifies themselves: who is
/// challenging, how long the duel is, and whether the seat is still open.
/// </summary>
public sealed record InviteDto(string Code, string ChallengerName, string ChallengerAvatar, int Questions, bool Open);
