using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class QuestionEditTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static Question New() => Question.Create("q1", Language.En, "geography", Difficulty.Easy,
        "Original prompt?", ["a", "b", "c", "d"], 0, T0, status: QuestionStatus.Pending);

    [Fact]
    public void Editing_changes_the_wording_and_the_answer()
    {
        var q = New();
        q.Edit(Language.En, "geography", Difficulty.Easy, "New prompt?", ["w", "x", "y", "z"], 2, null, "because");

        Assert.Equal("New prompt?", q.Prompt);
        Assert.Equal(2, q.CorrectIndex);
        Assert.Equal("because", q.Explanation);
    }

    [Fact]
    public void Editing_changes_the_difficulty()
    {
        var q = New();
        q.Edit(Language.En, "geography", Difficulty.Hard, "Original prompt?", ["a", "b", "c", "d"], 0, null, null);

        Assert.Equal(Difficulty.Hard, q.Level);
    }

    [Fact]
    public void Editing_moves_a_question_to_another_category()
    {
        var q = New();
        q.Edit(Language.En, "movies", Difficulty.Easy, "Original prompt?", ["a", "b", "c", "d"], 0, null, null);

        Assert.Equal("movies", q.CategoryId);
    }

    [Fact]
    public void Editing_corrects_a_mis_tagged_language()
    {
        var q = New();
        q.Edit(Language.Fa, "geography", Difficulty.Easy, "Original prompt?", ["a", "b", "c", "d"], 0, null, null);

        Assert.Equal(Language.Fa, q.Lang);
    }

    [Fact]
    public void Editing_still_refuses_a_broken_question()
    {
        var q = New();
        Assert.Throws<ArgumentException>(() => q.Edit(Language.En, "geography", Difficulty.Easy, "New?", ["a", "b"], 0, null, null));
        Assert.Equal("Original prompt?", q.Prompt);
    }

    [Fact]
    public void A_question_can_be_sent_back_to_pending()
    {
        var q = New();
        q.Approve();
        Assert.Equal(QuestionStatus.Approved, q.Status);

        q.SetStatus(QuestionStatus.Pending);
        Assert.Equal(QuestionStatus.Pending, q.Status);
    }

    [Fact]
    public void Editing_does_not_silently_change_the_status()
    {
        var q = New();
        q.Approve();
        q.Edit(Language.En, "geography", Difficulty.Hard, "Original prompt?", ["a", "b", "c", "d"], 0, null, null);

        Assert.Equal(QuestionStatus.Approved, q.Status);
    }
}
