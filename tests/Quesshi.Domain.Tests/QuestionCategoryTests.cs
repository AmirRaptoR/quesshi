using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public sealed class QuestionCategoryTests
{
    [Fact]
    public void Uncategorized_question_is_valid_through_create_edit_and_restore()
    {
        var now = DateTimeOffset.UtcNow;
        var question = Question.Create("q", Language.En, null, Difficulty.Easy, "Prompt?",
            ["a", "b", "c", "d"], 0, now);

        Assert.Null(question.CategoryId);
        question.Edit(Language.En, null, Difficulty.Easy, "Updated?", ["a", "b", "c", "d"], 0,
            MediaRef.None, null);
        Assert.Null(question.CategoryId);

        var restored = Question.Restore("q", Language.En, null, Difficulty.Easy, "Updated?",
            ["a", "b", "c", "d"], 0, MediaRef.None, null, QuestionStatus.Approved,
            QuestionSource.Admin, now, 0, 0);
        Assert.Null(restored.CategoryId);
    }
}
