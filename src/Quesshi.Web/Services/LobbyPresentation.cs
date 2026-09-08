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
}
