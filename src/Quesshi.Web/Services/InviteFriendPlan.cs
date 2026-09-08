using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// Home.razor's own render-mapping for "Invite a friend" — the one button through which every duel
/// this player starts by inviting someone (rather than being matched at random) is opened, live or
/// async, two seats or eight. Issue #53 closed the capacity gap first (a wider async lobby); the last
/// gap it left was live: <see cref="Api.CreateLiveAsync"/>/<c>POST /api/live/lobby</c> existed with no
/// UI in front of them at all, so a live lobby of more than two could not be created from the app.
/// Rather than a second button for "live invite" beside this one — the whole premise of the lobby work
/// is that a duel *is* a lobby and its kind is a property of it, not a fork in the entry points — the
/// kind is a second dial next to the capacity stepper, read here exactly the same way: pure functions
/// of (capacity, isLive) and a creation result, the same way <see cref="LobbyPresentation"/> is (see
/// its own remarks). What capacity-2-async does is provably untouched, and what anything else does is
/// provably the lobby, with no rendered Home.razor and no running server required to check either.
/// </summary>
public static class InviteFriendPlan
{
    /// <summary>
    /// Two seats, async is the plain duel this button has always created — <see cref="Api.CreateMatchAsync"/>,
    /// unchanged, landing the owner in the duel itself. Everything else needs the lobby instead: a
    /// wider async duel because seats fill in one at a time on their own schedule, and a live duel of
    /// any size — two seats included — because a live invite has no "already paired" shortcut the way
    /// random matchmaking does; someone still has to follow the code and the owner still has to press
    /// Start before either side can see a question.
    /// </summary>
    public static bool NeedsLobby(int capacity, bool isLive) => isLive || capacity > 2;

    /// <summary>Where a capacity-2, async create sends its owner — exactly what Home.razor always
    /// computed inline before this: CanPlay is true only when CreateMatchAsync's own random-matching
    /// already paired them with a waiting opponent, so there is already a card to answer.</summary>
    public static string Route(MatchSummaryDto match) => match.CanPlay ? $"/play/{match.Id}" : $"/duel/{match.Id}";

    /// <summary>Every other create — wider async, or live at any capacity — always lands its owner on
    /// the lobby they just opened, to share the code and press Start once the seats they asked for are
    /// filled. One route regardless of kind: <c>/lobby/{Code}</c> already tells live and async apart
    /// for itself (see Lobby.razor's own RefreshAsync, which asks the invite endpoint once and remembers
    /// the answer), so the caller here does not need to know which endpoint it just called.</summary>
    public static string LobbyRoute(string code) => $"/lobby/{code}";
}
