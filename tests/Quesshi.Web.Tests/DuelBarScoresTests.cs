using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public class DuelBarScoresTests
{
    private static LiveViewDto Sample(string challengerId, string? opponentId)
    {
        List<LivePlayerViewDto> players = [new(challengerId, 30, 3, 0)];
        if (opponentId is not null) players.Add(new(opponentId, 10, 1, 0));

        return new(
            "m1", challengerId, opponentId, "inprogress", "question", null, DateTimeOffset.UtcNow, 0, 5,
            players, [], null, false, null, DateTimeOffset.UtcNow, null,
            ChallengerName: "Amir", ChallengerAvatar: "seed-a",
            OpponentName: opponentId is null ? null : "Sara", OpponentAvatar: opponentId is null ? null : "seed-s");
    }

    [Fact]
    public void The_challenger_sees_their_own_score_as_mine()
    {
        var (mine, theirs) = DuelBarScores.Split(Sample("amir", "sara"), "amir");

        Assert.Equal(("amir", "Amir", "seed-a", 30), (mine.PlayerId, mine.Name, mine.Avatar, mine.Score));
        Assert.Equal(("sara", "Sara", "seed-s", 10), (theirs.PlayerId, theirs.Name, theirs.Avatar, theirs.Score));
    }

    [Fact]
    public void The_opponent_sees_the_same_duel_mirrored()
    {
        var (mine, theirs) = DuelBarScores.Split(Sample("amir", "sara"), "sara");

        Assert.Equal(("sara", "Sara", "seed-s", 10), (mine.PlayerId, mine.Name, mine.Avatar, mine.Score));
        Assert.Equal(("amir", "Amir", "seed-a", 30), (theirs.PlayerId, theirs.Name, theirs.Avatar, theirs.Score));
    }

    [Fact]
    public void An_empty_lobby_seat_scores_zero_rather_than_throwing()
    {
        var (_, theirs) = DuelBarScores.Split(Sample("amir", null), "amir");

        Assert.Equal(0, theirs.Score);
        Assert.Equal("", theirs.PlayerId);
    }
}
