using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// Where a guest reopening <c>/join/{code}</c> belongs, decided from the resolved invite rather than
/// the pin alone — see issue #102: a guest pinned to a finished duel A must still see B's join card
/// when the link they just opened names B, not be swept back to A.
/// </summary>
public static class JoinRedirect
{
    /// <summary>
    /// Null means "render the join card as usual". Anything else is the route to send a guest
    /// straight to, chosen only when they are pinned to exactly the match the resolved invite names.
    /// </summary>
    public static string? TargetFor(bool isGuest, string? guestMatchId, InviteDto? invite)
    {
        if (!isGuest || guestMatchId is not { Length: > 0 } pinned || invite is null || invite.MatchId != pinned)
            return null;

        return invite.Live ? $"/live/{pinned}" : $"/duel/{pinned}";
    }
}
