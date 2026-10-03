using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public sealed class QuestionOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Choices = ["one", "two", "three", "four"];

    [Fact]
    public void Trivia_requires_owner_for_player_source_and_rejects_owner_for_public_sources()
    {
        var owned = Question.Create("q1", Language.En, null, Difficulty.Easy, "Which?", Choices, 0, Now,
            source: QuestionSource.Player, ownerId: "player-1");

        Assert.Equal("player-1", owned.OwnerId);
        Assert.Throws<ArgumentException>(() => Question.Create("q2", Language.En, null, Difficulty.Easy,
            "Which?", Choices, 0, Now, source: QuestionSource.Player));
        Assert.Throws<ArgumentException>(() => Question.Create("q3", Language.En, null, Difficulty.Easy,
            "Which?", Choices, 0, Now, ownerId: "player-1"));
    }

    [Fact]
    public void Voting_requires_owner_for_player_source_and_rejects_owner_for_public_sources()
    {
        var owned = VotingQuestion.Create("v1", Language.En, null, "Who?", VotingAnswerSource.Participants,
            null, Now, source: QuestionSource.Player, ownerId: "player-1");

        Assert.Equal("player-1", owned.OwnerId);
        Assert.Throws<ArgumentException>(() => VotingQuestion.Create("v2", Language.En, null, "Who?",
            VotingAnswerSource.Participants, null, Now, source: QuestionSource.Player));
        Assert.Throws<ArgumentException>(() => VotingQuestion.Create("v3", Language.En, null, "Who?",
            VotingAnswerSource.Participants, null, Now, ownerId: "player-1"));
    }

    [Fact]
    public void Restore_enforces_the_same_owner_source_invariant()
    {
        Assert.Throws<ArgumentException>(() => Question.Restore("q", Language.En, null, Difficulty.Easy,
            "Which?", Choices, 0, MediaRef.None, null, QuestionStatus.Approved, QuestionSource.Player,
            Now, 0, 0));
        Assert.Throws<ArgumentException>(() => VotingQuestion.Restore("v", Language.En, null, "Who?",
            VotingAnswerSource.Participants, null, MediaRef.None, QuestionStatus.Approved,
            QuestionSource.Admin, null, Now, Now, 0, "player-1"));
    }
}
