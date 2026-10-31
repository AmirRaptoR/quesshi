using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>Someone the viewer may invite to the lobby, chosen by the page that hosts the panel.</summary>
public sealed record LobbyInvitee(string Id, string Name);

/// <summary>
/// What the shared lobby panel shows, decided from a <see cref="SessionView"/> alone so the owner,
/// Start and roster rules are unit-testable with a plain view and no rendered component. The owner is
/// the lowest seat: a lobby always starts with its creator in the first seat, and the owner leaving
/// ends the lobby, so no later view can have a different owner.
/// </summary>
public static class LobbyPresentation
{
    public const string LobbyStatus = "lobby";

    public static bool IsLobby(SessionView view) => view.Status == LobbyStatus;

    /// <summary>The seated players, lowest seat first; a windowed roster contributes its top entries only.</summary>
    public static IReadOnlyList<SessionRosterSeat> Seats(SessionView view)
    {
        var seats = view.Roster switch
        {
            CompleteRosterView complete => complete.Seats,
            WindowedRosterView windowed => windowed.Top,
            _ => [],
        };
        return [.. seats.Where(seat => seat.Role == "player").OrderBy(seat => seat.Seat)];
    }

    public static bool IsOwner(SessionView view) =>
        view.Me.Role == "player" && Seats(view) is [{ } first, ..] && first.Id == view.Me.Id;

    /// <summary>Two or more seated and only the owner; the server applies the format's own minimum and is the one enforcing it.</summary>
    public static bool CanStart(SessionView view) => IsLobby(view) && IsOwner(view) && Seats(view).Count >= 2;

    public static bool IsSeated(SessionView view, string playerId) => Seats(view).Any(seat => seat.Id == playerId);

    public enum SeatState { Host, Ready }

    public static SeatState SeatStateFor(SessionView view, SessionRosterSeat seat) =>
        Seats(view) is [{ } first, ..] && first.Id == seat.Id ? SeatState.Host : SeatState.Ready;

    public enum InviteTileState { Available, Invited, Joined }

    /// <summary>A seated invitee has nothing left to invite; otherwise whether this visit already invited them decides.</summary>
    public static InviteTileState InviteState(SessionView view, LobbyInvitee invitee, bool alreadyInvited)
    {
        if (IsSeated(view, invitee.Id)) return InviteTileState.Joined;
        return alreadyInvited ? InviteTileState.Invited : InviteTileState.Available;
    }

    /// <summary>The link a share action hands out: the tenant's own join route for the lobby's code.</summary>
    public static string ShareLink(string baseUri, SessionView view) =>
        $"{baseUri.TrimEnd('/')}/join/{Uri.EscapeDataString(view.Code)}";
}
