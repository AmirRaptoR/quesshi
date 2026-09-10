using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// The lobby page's-eye view of either duel kind, unified once. <c>LiveViewDto</c> and
/// <c>MatchSummaryDto</c> each carry <c>Participants</c>/<c>Capacity</c>/<c>Settings</c>/
/// <c>SettingsLocked</c> now (issue #53), but as different record shapes, under different property
/// names, and alongside a lot of gameplay-only fields the lobby page never reads — <c>Lobby.razor</c>
/// projects whichever DTO its last fetch or push returned into one of these with <see cref="From"/>,
/// and everything below reads only this. That is the whole reason the owner/roster/Start rules are
/// unit-testable with a plain record literal, no wire type and no running duel required.
/// </summary>
public sealed record LobbySnapshot(
    string MatchId, string Code, bool IsLive, bool Waiting, bool CanPlay,
    List<LiveParticipantDto> Participants, int Capacity,
    DuelSettingsDto Settings, bool SettingsLocked,
    /// <summary>A live lobby's own clock (<c>LiveRules.LobbyExpires</c>, 10 minutes from creation);
    /// null for an async lobby, whose 48-hour deadline is not worth counting down live.</summary>
    DateTimeOffset? LobbyEndsAt = null);

public static class LobbyPresentation
{
    /// <summary>A live duel is "waiting" for exactly as long as it sits in <c>LivePhase.Lobby</c> —
    /// once it leaves that phase (Start was pressed, or the last seat filled it automatically), there
    /// is nothing left for this page to show and the caller navigates to <c>/live/{id}</c>.</summary>
    public static LobbySnapshot From(LiveViewDto v) => new(
        v.Id, v.Code, IsLive: true, Waiting: v.Phase == "lobby", CanPlay: false,
        v.Participants, v.Capacity, v.Settings ?? new DuelSettingsDto("fa", 6, [], []), v.SettingsLocked,
        v.LobbyEndsAt);

    /// <summary>An async duel is "waiting" for exactly as long as its state is <c>AwaitingOpponent</c>
    /// — <c>MatchState</c>'s own lobby phase. <see cref="LobbySnapshot.CanPlay"/> carries
    /// <c>MatchSummaryDto.CanPlay</c> through unchanged: once it has started, the caller needs it to
    /// choose between <c>/play/{id}</c> (this player has not finished yet) and <c>/duel/{id}</c>
    /// (they have, or it is somebody else's turn to).</summary>
    public static LobbySnapshot From(MatchSummaryDto v) => new(
        v.Id, v.Code, IsLive: false, Waiting: v.State == "awaitingopponent", v.CanPlay,
        v.Participants ?? [], v.Capacity, v.Settings ?? new DuelSettingsDto(v.Lang, v.Questions, [], []), v.SettingsLocked);

    /// <summary>Every lobby's own invariant: <c>Participants[0]</c> is always whoever created it.</summary>
    public static bool IsOwner(LobbySnapshot s, string meId) => s.Participants.Count > 0 && s.Participants[0].PlayerId == meId;

    /// <summary>Two or more seated, and only the owner may press it. This only decides whether the
    /// button renders enabled — <c>ILiveMatchGrain.StartAsync</c>/<c>IMatchGrain.StartAsync</c> apply
    /// the identical rule server-side and are the ones actually enforcing it.</summary>
    public static bool CanStart(LobbySnapshot s, string meId) => IsOwner(s, meId) && s.Participants.Count >= 2;

    /// <summary>Whether the caller already holds a seat — the read path (issue #104) can return a
    /// snapshot for a visitor who has never joined, so <c>Lobby.razor</c> needs this to decide between
    /// today's roster-plus-Start view and a take-a-seat button.</summary>
    public static bool IsParticipant(LobbySnapshot s, string meId) => s.Participants.Any(p => p.PlayerId == meId);

    /// <summary>Whether every seat is already taken — decides whether an unseated visitor's
    /// take-a-seat button renders enabled or as <c>lobby.invite.full</c>.</summary>
    public static bool IsFull(LobbySnapshot s) => s.Participants.Count >= s.Capacity;

    /// <summary>Where a page holding this snapshot belongs once it is no longer waiting — read the
    /// other way, this is exactly what Duel.razor/Live.razor send a still-waiting visitor to instead:
    /// this page, at <c>/lobby/{Code}</c>.</summary>
    public static string TargetRoute(LobbySnapshot s) => s.IsLive
        ? $"/live/{s.MatchId}"
        : s.CanPlay ? $"/play/{s.MatchId}" : $"/duel/{s.MatchId}";

    /// <summary>
    /// The roster padded out to <see cref="LobbySnapshot.Capacity"/> seats with nulls for the empty
    /// ones — the design note this exists for: eight seats have to stay legible without an empty seat
    /// making a two-player lobby look sparser than it does today, so an empty seat is drawn as its own
    /// quiet placeholder rather than the roster simply being a shorter list on some duels than others.
    /// </summary>
    public static IReadOnlyList<LiveParticipantDto?> Seats(LobbySnapshot s)
    {
        var seats = new List<LiveParticipantDto?>(s.Participants);
        while (seats.Count < s.Capacity) seats.Add(null);
        return seats;
    }

    /// <summary>
    /// What a face tile in the roster grid says about the seat it draws (issue #90's replacement for
    /// the old text rows). There are exactly three states: the lobby's own owner, anyone else already
    /// seated, and a still-open seat — <c>Lobby.razor</c> reads this once per tile rather than
    /// re-deriving "is this the owner's seat" itself, so the one rule (<see cref="IsOwner"/>'s own
    /// <c>Participants[0]</c> invariant) has one reader instead of two that could drift apart.
    /// </summary>
    public enum SeatState { Host, Ready, Open }

    /// <summary><paramref name="seat"/> is one entry of <see cref="Seats"/> — null for a still-empty
    /// seat, which is always <see cref="SeatState.Open"/> regardless of who else is seated.</summary>
    public static SeatState SeatStateFor(LobbySnapshot s, LiveParticipantDto? seat)
    {
        if (seat is null) return SeatState.Open;
        return s.Participants.Count > 0 && seat.PlayerId == s.Participants[0].PlayerId ? SeatState.Host : SeatState.Ready;
    }

    /// <summary>
    /// How many columns the face-tile grid draws: three up to a three-seat lobby, four beyond it.
    /// Three across leaves a two- or three-player lobby's tiles sized generously rather than
    /// stretched thin; a bigger lobby (up to eight seats) needs the fourth column just to stay on one
    /// screen on a phone without scrolling, the same "read from two seats to eight" goal <see
    /// cref="Seats"/>'s own padding already serves.
    /// </summary>
    public static int RosterColumns(LobbySnapshot s) => s.Capacity <= 3 ? 3 : 4;

    /// <summary>
    /// What the invite avatar row shows for one of the owner's friends (issue #90's replacement for
    /// the old per-row chip/button). The checks are ordered by how permanent each fact is:
    /// <see cref="Joined"/> wins once true, because a friend already seated has nothing left to
    /// invite regardless of anything else; then <see cref="LinkOnly"/>, since a guest account is
    /// refused by <c>LobbyHub.OnConnectedAsync</c> outright, so no in-app invite could ever reach one
    /// no matter how many times this owner taps; only then does whether *this visit* already sent an
    /// invitation (<paramref name="alreadyInvited"/>, the caller's own <c>_invited</c> set) decide
    /// between <see cref="Invited"/> and <see cref="Available"/>.
    /// </summary>
    public enum InviteTileState { Available, Invited, Joined, LinkOnly }

    public static InviteTileState InviteState(LobbySnapshot s, FriendDto friend, bool alreadyInvited)
    {
        if (s.Participants.Any(p => p.PlayerId == friend.Id)) return InviteTileState.Joined;
        if (friend.IsGuest) return InviteTileState.LinkOnly;
        return alreadyInvited ? InviteTileState.Invited : InviteTileState.Available;
    }
}
