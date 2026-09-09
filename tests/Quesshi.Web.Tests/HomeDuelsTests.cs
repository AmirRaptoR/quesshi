using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The Offline tab's Your-turn / Waiting-on-them split (issue #88), covered as a pure function of a
/// list of summaries rather than through a rendered <c>Home.razor</c> — the same way every other
/// render-mapping in this project is (see <see cref="LobbyPresentation"/>'s own remarks).
/// </summary>
public class HomeDuelsTests
{
    private static PlayerSideDto Side(string id, int answered = 0, int score = 0, bool finished = false)
        => new(id, id, $"seed-{id}", score, answered, answered, finished);

    private static MatchSummaryDto Duel(string id, bool canPlay, bool isLive = false, string? opponent = "them",
        int questions = 10, int answered = 0, int minutesAgo = 0)
        => new(id, $"C{id}", "en", "inprogress",
            Side("me", answered), opponent is null ? null : Side(opponent),
            null, false, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), canPlay, false, "",
            questions, isLive);

    [Fact]
    public void Duels_this_player_can_answer_are_their_turn()
    {
        var split = HomeDuels.Split([Duel("a", canPlay: true), Duel("b", canPlay: false)]);

        Assert.Equal(["a"], split.YourTurn.Select(m => m.Id));
        Assert.Equal(["b"], split.WaitingOnThem.Select(m => m.Id));
    }

    /// <summary>A live duel advances on its own clock — it is never CanPlay, so filing it under
    /// "waiting on them" would tell the player to wait out something that is not waiting for anyone.
    /// It belongs to the other tab entirely.</summary>
    [Fact]
    public void Live_duels_appear_on_neither_list()
    {
        var split = HomeDuels.Split([Duel("live", canPlay: false, isLive: true), Duel("async", canPlay: false)]);

        Assert.Empty(split.YourTurn);
        Assert.Equal(["async"], split.WaitingOnThem.Select(m => m.Id));
    }

    [Fact]
    public void Each_list_is_newest_first()
    {
        var split = HomeDuels.Split([
            Duel("old", canPlay: true, minutesAgo: 90),
            Duel("new", canPlay: true, minutesAgo: 2),
            Duel("middle", canPlay: true, minutesAgo: 30)
        ]);

        Assert.Equal(["new", "middle", "old"], split.YourTurn.Select(m => m.Id));
    }

    /// <summary>A duel nobody has joined yet is still waiting on somebody — it is just that the
    /// somebody has no name yet. It must not vanish, because its code is the only way anyone will
    /// ever find it.</summary>
    [Fact]
    public void A_duel_with_no_opponent_yet_is_waiting()
    {
        var split = HomeDuels.Split([Duel("open", canPlay: false, opponent: null)]);

        Assert.Equal(["open"], split.WaitingOnThem.Select(m => m.Id));
    }

    [Fact]
    public void Nothing_in_play_gives_two_empty_lists()
    {
        var split = HomeDuels.Split([]);

        Assert.Empty(split.YourTurn);
        Assert.Empty(split.WaitingOnThem);
    }

    [Fact]
    public void Questions_left_counts_down_from_the_duels_own_length()
    {
        Assert.Equal(10, HomeDuels.QuestionsLeft(Duel("a", true, questions: 10, answered: 0)));
        Assert.Equal(6, HomeDuels.QuestionsLeft(Duel("a", true, questions: 10, answered: 4)));
        Assert.Equal(0, HomeDuels.QuestionsLeft(Duel("a", true, questions: 10, answered: 10)));
    }

    /// <summary>A question retired after the duel was drawn would otherwise render "-2 questions left".</summary>
    [Fact]
    public void Questions_left_never_goes_negative()
        => Assert.Equal(0, HomeDuels.QuestionsLeft(Duel("a", true, questions: 6, answered: 8)));

    [Fact]
    public void A_playable_row_opens_the_duel_and_a_waiting_one_opens_its_standing()
    {
        Assert.Equal("/play/a", HomeDuels.Route(Duel("a", canPlay: true)));
        Assert.Equal("/duel/b", HomeDuels.Route(Duel("b", canPlay: false)));
    }
}
