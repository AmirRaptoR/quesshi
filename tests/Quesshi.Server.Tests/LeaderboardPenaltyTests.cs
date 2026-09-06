namespace Quesshi.Server.Tests;

/// <summary>
/// The zero floor on <see cref="Quesshi.Application.Ports.ILeaderboard.PenaliseAsync"/>: a penalty can
/// never take a score negative, and two of them landing at once cannot race past zero either.
/// </summary>
public class LeaderboardPenaltyTests
{
    [Fact]
    public async Task A_penalty_bigger_than_the_score_leaves_exactly_zero()
    {
        var board = new FakeLeaderboard();
        await board.AddAsync("p-floor-1", 50);

        await board.PenaliseAsync("p-floor-1", 200);

        Assert.Equal(0, board.Scores["p-floor-1"]);
    }

    [Fact]
    public async Task A_penalty_never_takes_a_score_below_zero()
    {
        var board = new FakeLeaderboard();
        await board.AddAsync("p-floor-2", 100);

        await board.PenaliseAsync("p-floor-2", 40);
        Assert.Equal(60, board.Scores["p-floor-2"]);

        await board.PenaliseAsync("p-floor-2", 1000);
        Assert.Equal(0, board.Scores["p-floor-2"]);
    }

    [Fact]
    public async Task Concurrent_penalties_cannot_race_a_score_below_zero()
    {
        var board = new FakeLeaderboard();
        await board.AddAsync("p-floor-race", 1000);

        // Ten penalties of 200 land at once against a score of 1000: sequentially that is exactly
        // zero, but a race that reads-then-writes without serialising could land negative.
        var penalties = Enumerable.Range(0, 10).Select(_ => board.PenaliseAsync("p-floor-race", 200));
        await Task.WhenAll(penalties);

        Assert.Equal(0, board.Scores["p-floor-race"]);
    }

    [Fact]
    public async Task Concurrent_penalties_still_add_up_correctly_when_the_score_has_room()
    {
        var board = new FakeLeaderboard();
        await board.AddAsync("p-floor-sum", 10_000);

        var penalties = Enumerable.Range(0, 10).Select(_ => board.PenaliseAsync("p-floor-sum", 100));
        await Task.WhenAll(penalties);

        Assert.Equal(9_000, board.Scores["p-floor-sum"]);
    }
}
