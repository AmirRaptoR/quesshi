using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// Issue #53's lobby page: owner/non-owner rendering, Start's two-or-more gate, and settings going
/// read-only once the question set is drawn — the acceptance criteria the issue names, covered here
/// as pure functions of <see cref="LobbySnapshot"/> rather than through a rendered <c>Lobby.razor</c>,
/// the same way every other render-mapping in this project is (see <see cref="LobbyPresentation"/>'s
/// own remarks).
/// </summary>
public class LobbyPresentationTests
{
    private static LiveParticipantDto P(string id, bool guest = false) => new(id, id, $"seed-{id}", guest);

    private static LiveViewDto LiveSample(List<LiveParticipantDto> participants, int capacity = 4,
        string phase = "lobby", int questionCount = 6, List<string>? categories = null, List<int>? levels = null,
        bool settingsLocked = false)
        => new("m1", participants, "inprogress", phase, null, DateTimeOffset.UtcNow, 0, 0, [], [], [],
            null, false, [], DateTimeOffset.UtcNow, null, "CODE01", null, null, null,
            capacity, new DuelSettingsDto("en", questionCount, categories ?? [], levels ?? []), settingsLocked);

    private static MatchSummaryDto AsyncSample(List<LiveParticipantDto> participants, int capacity = 4,
        string state = "awaitingopponent", bool canPlay = false, int questionCount = 6, bool settingsLocked = false)
        => new("m1", "CODE01", "en", state, new PlayerSideDto(participants[0].PlayerId, participants[0].Name, participants[0].Avatar, 0, 0, 0, false),
            null, null, false, DateTimeOffset.UtcNow, canPlay, false, "pending", questionCount, IsLive: false,
            Participants: participants, Capacity: capacity,
            Settings: new DuelSettingsDto("en", questionCount, [], []), SettingsLocked: settingsLocked);

    [Fact]
    public void The_owner_is_always_the_first_seated_participant()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("sara")]));

        Assert.True(LobbyPresentation.IsOwner(snapshot, "amir"));
        Assert.False(LobbyPresentation.IsOwner(snapshot, "sara"));
    }

    [Fact]
    public void A_non_owner_is_never_told_they_can_start_even_with_enough_players()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("sara")]));

        Assert.False(LobbyPresentation.CanStart(snapshot, "sara"));
    }

    [Fact]
    public void The_owner_alone_cannot_start()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir")]));

        Assert.True(LobbyPresentation.IsOwner(snapshot, "amir"));
        Assert.False(LobbyPresentation.CanStart(snapshot, "amir"));
    }

    [Fact]
    public void The_owner_with_two_or_more_seated_can_start()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("sara")]));

        Assert.True(LobbyPresentation.CanStart(snapshot, "amir"));
    }

    [Fact]
    public void The_owner_of_a_bigger_lobby_can_start_the_moment_a_second_seat_fills()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("sara")], capacity: 8));

        Assert.True(LobbyPresentation.CanStart(snapshot, "amir"));
    }

    // ---- IsParticipant / IsFull: the read path's own take-a-seat states (issue #104) ----

    [Fact]
    public void A_seated_player_is_a_participant_and_a_visitor_is_not()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("sara")], capacity: 3));

        Assert.True(LobbyPresentation.IsParticipant(snapshot, "amir"));
        Assert.True(LobbyPresentation.IsParticipant(snapshot, "sara"));
        Assert.False(LobbyPresentation.IsParticipant(snapshot, "stranger"));
    }

    [Fact]
    public void A_lobby_with_an_open_seat_is_not_full()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("sara")], capacity: 3));

        Assert.False(LobbyPresentation.IsFull(snapshot));
    }

    [Fact]
    public void A_lobby_seated_to_capacity_is_full()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("sara")], capacity: 2));

        Assert.True(LobbyPresentation.IsFull(snapshot));
    }

    [Fact]
    public void Settings_are_locked_once_the_question_set_is_drawn_for_a_live_lobby()
    {
        // TotalRounds (here 0, meaning no rounds/questions drawn) is what Mappers.ToLiveDtoAsync
        // reads to compute SettingsLocked server-side; this proves the client trusts that flag
        // directly rather than re-deriving it from some other field.
        var drawn = LobbyPresentation.From(LiveSample([P("amir"), P("sara")], settingsLocked: true));
        var notDrawn = LobbyPresentation.From(LiveSample([P("amir"), P("sara")], settingsLocked: false));

        Assert.True(drawn.SettingsLocked);
        Assert.False(notDrawn.SettingsLocked);
    }

    [Fact]
    public void Settings_are_locked_for_a_legacy_async_record_that_arrives_with_questions_already_drawn()
    {
        // The doc's own rule: "settings are editable exactly while QuestionIds is empty" — including
        // for a legacy record converted into a lobby whose questions were drawn long before this
        // design existed. There is no second flag; SettingsLocked is that same rule, wire-side.
        var snapshot = LobbyPresentation.From(AsyncSample([P("amir"), P("sara")], settingsLocked: true));

        Assert.True(snapshot.SettingsLocked);
    }

    [Fact]
    public void A_live_lobby_still_waiting_reports_waiting()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir")], phase: "lobby"));

        Assert.True(snapshot.Waiting);
    }

    [Fact]
    public void A_live_duel_no_longer_in_its_lobby_phase_reports_not_waiting_and_targets_live()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("sara")], phase: "countdown"));

        Assert.False(snapshot.Waiting);
        Assert.Equal("/live/m1", LobbyPresentation.TargetRoute(snapshot));
    }

    [Fact]
    public void An_async_lobby_still_awaiting_an_opponent_reports_waiting()
    {
        var snapshot = LobbyPresentation.From(AsyncSample([P("amir")], state: "awaitingopponent"));

        Assert.True(snapshot.Waiting);
    }

    [Fact]
    public void A_started_async_duel_this_player_can_still_play_targets_play_not_duel()
    {
        var snapshot = LobbyPresentation.From(AsyncSample([P("amir"), P("sara")], state: "inprogress", canPlay: true));

        Assert.False(snapshot.Waiting);
        Assert.Equal("/play/m1", LobbyPresentation.TargetRoute(snapshot));
    }

    [Fact]
    public void A_started_async_duel_this_player_has_finished_targets_the_results_screen()
    {
        var snapshot = LobbyPresentation.From(AsyncSample([P("amir"), P("sara")], state: "inprogress", canPlay: false));

        Assert.Equal("/duel/m1", LobbyPresentation.TargetRoute(snapshot));
    }

    [Fact]
    public void Seats_are_padded_out_to_capacity_with_empty_placeholders()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("sara")], capacity: 5));

        var seats = LobbyPresentation.Seats(snapshot);

        Assert.Equal(5, seats.Count);
        Assert.Equal(["amir", "sara"], seats.Take(2).Select(s => s?.PlayerId));
        Assert.All(seats.Skip(2), Assert.Null);
    }

    /// <summary>The design note this exists for: a two-player lobby must not look emptier than it does
    /// today, which is exactly what padding a capacity-2 lobby to eight seats would do — it stays
    /// exactly the two rows it always had.</summary>
    [Fact]
    public void A_capacity_two_lobby_pads_to_no_more_than_two_seats()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir")], capacity: 2));

        Assert.Equal(2, LobbyPresentation.Seats(snapshot).Count);
    }

    // --- issue #90: the roster as face tiles, and the invite row as an avatar row ------------------

    [Fact]
    public void The_owners_own_seat_tile_reads_host()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("sara")]));

        Assert.Equal(LobbyPresentation.SeatState.Host, LobbyPresentation.SeatStateFor(snapshot, snapshot.Participants[0]));
    }

    [Fact]
    public void Anyone_else_seated_reads_ready()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("sara")]));

        Assert.Equal(LobbyPresentation.SeatState.Ready, LobbyPresentation.SeatStateFor(snapshot, snapshot.Participants[1]));
    }

    [Fact]
    public void A_still_empty_seat_reads_open()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir")], capacity: 3));
        var seats = LobbyPresentation.Seats(snapshot);

        Assert.Equal(LobbyPresentation.SeatState.Open, LobbyPresentation.SeatStateFor(snapshot, seats[1]));
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    public void The_roster_grid_draws_three_columns_up_to_a_three_seat_lobby(int capacity, int expectedColumns)
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir")], capacity: capacity));

        Assert.Equal(expectedColumns, LobbyPresentation.RosterColumns(snapshot));
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(8, 4)]
    public void The_roster_grid_draws_four_columns_beyond_a_three_seat_lobby(int capacity, int expectedColumns)
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir")], capacity: capacity));

        Assert.Equal(expectedColumns, LobbyPresentation.RosterColumns(snapshot));
    }

    private static FriendDto Friend(string id, bool online = false, bool guest = false)
        => new(id, id, $"seed-{id}", Score: 0, Online: online, IsGuest: guest);

    [Fact]
    public void An_invite_tile_is_available_for_a_friend_not_yet_touched()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir")], capacity: 3));

        Assert.Equal(LobbyPresentation.InviteTileState.Available,
            LobbyPresentation.InviteState(snapshot, Friend("cam"), alreadyInvited: false));
    }

    [Fact]
    public void An_invite_tile_is_invited_once_this_visit_has_sent_one()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir")], capacity: 3));

        Assert.Equal(LobbyPresentation.InviteTileState.Invited,
            LobbyPresentation.InviteState(snapshot, Friend("cam"), alreadyInvited: true));
    }

    /// <summary>A guest friend can never receive an in-app invite — <c>LobbyHub.OnConnectedAsync</c>
    /// refuses every guest connection outright — so the tile says "link only" instead of offering a
    /// button that could never do anything, whether or not this visit already tried.</summary>
    [Fact]
    public void An_invite_tile_is_link_only_for_a_guest_friend_even_if_already_invited()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir")], capacity: 3));

        Assert.Equal(LobbyPresentation.InviteTileState.LinkOnly,
            LobbyPresentation.InviteState(snapshot, Friend("cam", guest: true), alreadyInvited: true));
    }

    /// <summary>A friend who is already seated has nothing left to invite — this wins over "invited"
    /// even when this same visit is the one that sent the invitation that got them there.</summary>
    [Fact]
    public void An_invite_tile_is_joined_once_the_friend_is_seated()
    {
        var snapshot = LobbyPresentation.From(LiveSample([P("amir"), P("cam")], capacity: 3));

        Assert.Equal(LobbyPresentation.InviteTileState.Joined,
            LobbyPresentation.InviteState(snapshot, Friend("cam"), alreadyInvited: true));
    }
}
