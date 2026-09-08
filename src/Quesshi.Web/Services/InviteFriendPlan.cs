using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// Home.razor's own render-mapping for "Invite a friend" — the last gap issue #53 left open: the
/// lobby page, the endpoints and the grains have all been N-player for a while, but the button that
/// starts one only ever called <see cref="Api.CreateMatchAsync"/>, which is hard-coded to two seats.
/// Pulled out as pure functions of a capacity and a creation result, the same way
/// <see cref="LobbyPresentation"/> is (see its own remarks): what capacity 2 does is provably
/// untouched, and what capacity above 2 does is provably the lobby, with no rendered Home.razor and
/// no running server required to check either.
/// </summary>
public static class InviteFriendPlan
{
    /// <summary>
    /// Two seats is the plain duel this button has always created — <see cref="Api.CreateMatchAsync"/>,
    /// unchanged, landing the owner in the duel itself. Anything wider needs the lobby instead: seats
    /// fill in one at a time, on their own schedule, so there is no opponent yet to land the owner
    /// beside and no question set to draw until the owner presses Start.
    /// </summary>
    public static bool NeedsLobby(int capacity) => capacity > 2;

    /// <summary>Where a capacity-2 create sends its owner — exactly what Home.razor always computed
    /// inline before this: CanPlay is true only when CreateMatchAsync's own random-matching already
    /// paired them with a waiting opponent, so there is already a card to answer.</summary>
    public static string Route(MatchSummaryDto match) => match.CanPlay ? $"/play/{match.Id}" : $"/duel/{match.Id}";

    /// <summary>A capacity-above-2 create always lands its owner on the lobby they just opened, to
    /// share the code and press Start once the seats they asked for are filled.</summary>
    public static string LobbyRoute(string code) => $"/lobby/{code}";
}
