using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// Issue #88's own acceptance criterion — "every existing create/join path is still reachable" —
/// written down. The home lost thirty-seven controls; what it must not have lost is a door, and this
/// is the table saying which door leads where. <c>Home.razor</c> dispatches on
/// <see cref="HomeEntryPlan.Call"/>, so these are the calls the buttons actually make rather than a
/// description of them kept alongside.
/// </summary>
public class HomeEntryPlanTests
{
    [Theory]
    [InlineData(HomeEntry.CreateLobby, HomeCall.CreateLiveLobby)]
    [InlineData(HomeEntry.PlayAStranger, HomeCall.QueueLiveRandom)]
    [InlineData(HomeEntry.ChallengeFriend, HomeCall.ChallengeOverHub)]
    [InlineData(HomeEntry.NewDuelFriend, HomeCall.CreateAsyncDuel)]
    [InlineData(HomeEntry.NewDuelAnyone, HomeCall.CreateAsyncRandomDuel)]
    public void Each_entry_point_makes_its_own_call(HomeEntry entry, HomeCall call)
        => Assert.Equal(call, HomeEntryPlan.Call(entry));

    /// <summary>
    /// A code identifies a duel; the tab the player happened to be looking at does not. Both code
    /// fields therefore resolve the invite first and join whichever kind it turns out to be — pasting
    /// a live code on the Offline tab joins the live duel rather than failing, which is the only
    /// behaviour that could be right for someone who was simply sent a code.
    /// </summary>
    [Theory]
    [InlineData(HomeEntry.JoinWithCode)]
    [InlineData(HomeEntry.NewDuelByCode)]
    public void Both_code_fields_resolve_the_invite_before_joining(HomeEntry entry)
        => Assert.Equal(HomeCall.JoinByCode, HomeEntryPlan.Call(entry));

    /// <summary>Every path the old Play card could take is still one of these — a live lobby, the
    /// live queue, a friend challenge, an async duel invited or matched, and a join by code.</summary>
    [Fact]
    public void Every_call_the_home_can_make_is_reachable_from_some_entry_point()
    {
        var reached = Enum.GetValues<HomeEntry>().Select(HomeEntryPlan.Call).ToHashSet();

        Assert.Equal(Enum.GetValues<HomeCall>().ToHashSet(), reached);
    }

    [Fact]
    public void Every_entry_point_belongs_to_a_tab_that_exists()
    {
        var tabs = Enum.GetValues<HomeEntry>().Select(HomeEntryPlan.Tab).Distinct().Order().ToList();

        Assert.Equal([HomeTabs.Live, HomeTabs.Offline], tabs);
    }

    [Theory]
    [InlineData(HomeEntry.CreateLobby)]
    [InlineData(HomeEntry.JoinWithCode)]
    [InlineData(HomeEntry.PlayAStranger)]
    [InlineData(HomeEntry.ChallengeFriend)]
    public void The_live_tab_keeps_the_four_live_paths(HomeEntry entry)
        => Assert.Equal(HomeTabs.Live, HomeEntryPlan.Tab(entry));

    [Theory]
    [InlineData(HomeEntry.NewDuelFriend)]
    [InlineData(HomeEntry.NewDuelAnyone)]
    [InlineData(HomeEntry.NewDuelByCode)]
    public void The_offline_tab_keeps_the_three_turn_based_paths(HomeEntry entry)
        => Assert.Equal(HomeTabs.Offline, HomeEntryPlan.Tab(entry));

    /// <summary>Two seats, the value the old stepper started on: seats are chosen in the lobby now,
    /// so this is what "Create a lobby" opens with rather than something the home asks for.</summary>
    [Fact]
    public void A_lobby_opens_at_two_seats()
        => Assert.Equal(2, HomeEntryPlan.DefaultCapacity);
}
