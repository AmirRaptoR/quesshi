using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public class DuelBarScoresTests
{
    private static LiveViewDto Sample(string challengerId, string? opponentId, params string[] extraIds)
    {
        List<LiveParticipantDto> participants = [new(challengerId, "Amir", "seed-a", false)];
        List<LivePlayerViewDto> players = [new(challengerId, 30, 3, 0)];

        if (opponentId is not null)
        {
            participants.Add(new(opponentId, "Sara", "seed-s", false));
            players.Add(new(opponentId, 10, 1, 0));
        }

        foreach (var extraId in extraIds)
        {
            participants.Add(new(extraId, $"Guest-{extraId}", $"seed-{extraId}", false));
            players.Add(new(extraId, 5, 0, 0));
        }

        return new(
            "m1", participants, "inprogress", "question", null, DateTimeOffset.UtcNow, 0, 5,
            players, [], [], null, false, null, DateTimeOffset.UtcNow, null);
    }

    [Fact]
    public void The_challenger_sees_their_own_score_as_mine_and_the_opponent_as_the_one_other_side()
    {
        var (mine, others) = DuelBarScores.Split(Sample("amir", "sara"), "amir");

        Assert.Equal(("amir", "Amir", "seed-a", 30), (mine.PlayerId, mine.Name, mine.Avatar, mine.Score));
        var theirs = Assert.Single(others);
        Assert.Equal(("sara", "Sara", "seed-s", 10), (theirs.PlayerId, theirs.Name, theirs.Avatar, theirs.Score));
    }

    [Fact]
    public void The_opponent_sees_the_same_duel_mirrored()
    {
        var (mine, others) = DuelBarScores.Split(Sample("amir", "sara"), "sara");

        Assert.Equal(("sara", "Sara", "seed-s", 10), (mine.PlayerId, mine.Name, mine.Avatar, mine.Score));
        var theirs = Assert.Single(others);
        Assert.Equal(("amir", "Amir", "seed-a", 30), (theirs.PlayerId, theirs.Name, theirs.Avatar, theirs.Score));
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
