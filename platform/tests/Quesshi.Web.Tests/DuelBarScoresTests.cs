using Quesshi.Shared;
using Quesshi.Web.Plugins.Formats.AsyncDuel;

namespace Quesshi.Web.Tests;

public class DuelBarScoresTests
{
    private static IReadOnlyList<SessionRosterSeat> Sample(string challengerId, string? opponentId, params string[] extraIds)
    {
        List<SessionRosterSeat> seats = [new(challengerId, "Amir", 0, "player", "done", 30, null, null)];
        if (opponentId is not null) seats.Add(new(opponentId, "Sara", 1, "player", "done", 10, null, null));
        foreach (var extraId in extraIds) seats.Add(new(extraId, $"Guest-{extraId}", seats.Count, "player", "done", 5, null, null));
        return seats;
    }

    [Fact]
    public void The_challenger_sees_their_own_score_as_mine_and_the_opponent_as_the_one_other_side()
    {
        var (mine, others) = DuelBarScores.Split(Sample("amir", "sara"), "amir");

        Assert.Equal(("amir", "Amir", 30), (mine!.PlayerId, mine.Name, mine.Score));
        var theirs = Assert.Single(others);
        Assert.Equal(("sara", "Sara", 10), (theirs.PlayerId, theirs.Name, theirs.Score));
    }

    [Fact]
    public void The_opponent_sees_the_same_duel_mirrored()
    {
        var (mine, others) = DuelBarScores.Split(Sample("amir", "sara"), "sara");

        Assert.Equal(("sara", "Sara", 10), (mine!.PlayerId, mine.Name, mine.Score));
        var theirs = Assert.Single(others);
        Assert.Equal(("amir", "Amir", 30), (theirs.PlayerId, theirs.Name, theirs.Score));
    }

    [Fact]
    public void An_empty_lobby_seat_yields_no_other_side_rather_than_a_phantom_one()
    {
        var (_, others) = DuelBarScores.Split(Sample("amir", null), "amir");

        Assert.Empty(others);
    }

    [Fact]
    public void A_third_seat_appears_alongside_the_second_in_join_order()
    {
        var (_, others) = DuelBarScores.Split(Sample("amir", "sara", "reza"), "amir");

        Assert.Equal(["sara", "reza"], others.Select(o => o.PlayerId));
        Assert.Equal(5, others[1].Score);
    }
}
