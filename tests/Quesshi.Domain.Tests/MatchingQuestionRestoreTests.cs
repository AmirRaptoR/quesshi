using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchingQuestionRestoreTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = CreatedAt.AddDays(3);

    [Fact]
    public void Restore_performs_no_validation_for_a_stored_row_with_too_many_choices()
    {
        var nineChoices = Enumerable.Range(1, 9).Select(i => $"choice{i}").ToArray();

        var q = MatchingQuestion.Restore("mq1", Language.En, "movies", "Match each actor.",
            MatchingAnswerSource.Fixed, nineChoices, MediaRef.None,
            QuestionStatus.Approved, QuestionSource.Admin, null, CreatedAt, UpdatedAt, 5);

        Assert.Equal(9, q.FixedChoices.Count);
    }

    [Fact]
    public void Restore_performs_no_validation_for_a_stored_fixed_row_with_no_choices()
    {
        var q = MatchingQuestion.Restore("mq1", Language.En, "movies", "stored",
            MatchingAnswerSource.Fixed, [], MediaRef.None,
            QuestionStatus.Approved, QuestionSource.Admin, null, CreatedAt, UpdatedAt, 5);

        Assert.Empty(q.FixedChoices);
    }

    [Fact]
    public void Restore_performs_no_validation_for_a_stored_row_with_a_blank_prompt()
    {
        var q = MatchingQuestion.Restore("mq1", Language.En, "movies", "   ",
            MatchingAnswerSource.Participants, [], MediaRef.None,
            QuestionStatus.Approved, QuestionSource.Admin, null, CreatedAt, UpdatedAt, 5);

        Assert.Equal("   ", q.Prompt);
    }

    [Fact]
    public void Restore_takes_CreatedAt_UpdatedAt_TimesServed_Status_Source_and_Topic_from_storage()
    {
        var q = MatchingQuestion.Restore("mq1", Language.En, "movies", "Match each actor.",
            MatchingAnswerSource.Participants, [], MediaRef.None,
            QuestionStatus.Rejected, QuestionSource.Seed, "actor|role", CreatedAt, UpdatedAt, 7);

        Assert.Equal(CreatedAt, q.CreatedAt);
        Assert.Equal(UpdatedAt, q.UpdatedAt);
        Assert.Equal(7, q.TimesServed);
        Assert.Equal(QuestionStatus.Rejected, q.Status);
        Assert.Equal(QuestionSource.Seed, q.Source);
        Assert.Equal("actor|role", q.Topic);
    }

    [Fact]
    public void Restore_coalesces_null_choices_without_copying_or_validating()
    {
        var q = MatchingQuestion.Restore("mq1", Language.En, "movies", "   ",
            MatchingAnswerSource.Fixed, null, MediaRef.None,
            QuestionStatus.Pending, QuestionSource.Admin, null, CreatedAt, UpdatedAt, 0);

        Assert.Empty(q.FixedChoices);
    }

    [Fact]
    public void Restore_keeps_the_choices_reference_and_does_not_normalize_it()
    {
        var choices = new List<string> { "  first  ", "second" };
        var q = MatchingQuestion.Restore("mq1", Language.En, "movies", " prompt ",
            MatchingAnswerSource.Fixed, choices, MediaRef.None,
            QuestionStatus.Pending, QuestionSource.Admin, null, CreatedAt, UpdatedAt, 0);

        Assert.Same(choices, q.FixedChoices);
        Assert.Equal(" prompt ", q.Prompt);
        choices[0] = "changed";
        Assert.Equal("changed", q.FixedChoices[0]);
    }
}
