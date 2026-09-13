using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchingQuestionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static MatchingQuestion NewParticipants() => MatchingQuestion.Create("mq1", Language.En, "movies",
        "Match each actor to their role.", MatchingAnswerSource.Participants, null,
        T0, source: QuestionSource.Admin, status: QuestionStatus.Pending);

    private static MatchingQuestion NewFixed(IReadOnlyList<string>? choices = null) => MatchingQuestion.Create(
        "mq1", Language.En, "movies", "Match each actor to their role.", MatchingAnswerSource.Fixed,
        choices ?? ["Hero", "Sidekick", "Villain"], T0, source: QuestionSource.Admin, status: QuestionStatus.Pending);

    [Fact]
    public void Create_starts_TimesServed_at_zero_regardless_of_caller_input()
    {
        var q = NewParticipants();

        Assert.Equal(0, q.TimesServed);
    }

    [Fact]
    public void Participants_with_choices_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => MatchingQuestion.Create("mq1", Language.En, "movies",
            "Match each actor to their role.", MatchingAnswerSource.Participants, ["a", "b"],
            T0, source: QuestionSource.Admin, status: QuestionStatus.Pending));

        Assert.Equal("choices", ex.ParamName);
    }

    [Fact]
    public void Participants_with_no_choices_succeeds_with_empty_FixedChoices()
    {
        var q = NewParticipants();

        Assert.Empty(q.FixedChoices);
    }

    [Theory]
    [InlineData(MatchingRules.MinFixedChoices)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(MatchingRules.MaxFixedChoices)]
    public void Fixed_succeeds_for_two_through_eight_choices(int count)
    {
        var choices = Enumerable.Range(1, count).Select(i => $"choice{i}").ToArray();

        var q = NewFixed(choices);

        Assert.Equal(count, q.FixedChoices.Count);
    }

    [Theory]
    [InlineData(MatchingRules.MinFixedChoices - 2)]
    [InlineData(MatchingRules.MinFixedChoices - 1)]
    [InlineData(MatchingRules.MaxFixedChoices + 1)]
    public void Fixed_rejects_out_of_range_choice_counts(int count)
    {
        var choices = Enumerable.Range(1, count).Select(i => $"choice{i}").ToArray();

        Assert.Throws<ArgumentException>(() => NewFixed(choices));
    }

    [Fact]
    public void Null_FixedChoices_is_valid_for_Participants()
    {
        var q = MatchingQuestion.Create("mq1", Language.En, "movies", "Match each actor to their role.",
            MatchingAnswerSource.Participants, null, T0, source: QuestionSource.Admin, status: QuestionStatus.Pending);

        Assert.Empty(q.FixedChoices);
    }

    [Fact]
    public void Null_FixedChoices_is_rejected_as_too_few_for_Fixed()
    {
        Assert.Throws<ArgumentException>(() => MatchingQuestion.Create("mq1", Language.En, "movies",
            "Match each actor to their role.", MatchingAnswerSource.Fixed, null,
            T0, source: QuestionSource.Admin, status: QuestionStatus.Pending));
    }

    [Fact]
    public void Unknown_answer_source_is_rejected_by_name()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => MatchingQuestion.Create("mq1", Language.En, "movies",
            "Match each actor to their role.", (MatchingAnswerSource)42, null, T0));

        Assert.Equal("answerSource", ex.ParamName);
    }

    [Fact]
    public void Create_copies_the_choices_list()
    {
        var choices = new List<string> { "Hero", "Villain" };
        var q = MatchingQuestion.Create("mq1", Language.En, "movies", "Match each actor to their role.",
            MatchingAnswerSource.Fixed, choices, T0);

        choices[0] = "Changed";
        Assert.Equal(["Hero", "Villain"], q.FixedChoices);
    }

    [Fact]
    public void Create_accepts_arbitrary_identity_values_trims_prompt_and_defaults_media()
    {
        var q = MatchingQuestion.Create("", Language.En, "not-m-prefixed", "  Prompt  ",
            MatchingAnswerSource.Participants, null, T0);

        Assert.Equal("", q.Id);
        Assert.Equal("not-m-prefixed", q.MatchingCategoryId);
        Assert.Equal("Prompt", q.Prompt);
        Assert.Equal(MediaRef.None, q.Media);
    }

    [Fact]
    public void Validate_reports_the_expected_parameter_for_prompt_and_choices()
    {
        var prompt = Assert.Throws<ArgumentException>(() => MatchingQuestion.Validate("  ",
            MatchingAnswerSource.Participants, null));
        Assert.Equal("prompt", prompt.ParamName);

        var choices = Assert.Throws<ArgumentException>(() => MatchingQuestion.Validate("Prompt",
            MatchingAnswerSource.Participants, ["a"]));
        Assert.Equal("choices", choices.ParamName);
    }

    [Fact]
    public void Fixed_rejects_a_blank_choice()
    {
        Assert.Throws<ArgumentException>(() => NewFixed(["Hero", "  ", "Villain"]));
    }

    [Fact]
    public void Fixed_rejects_choices_that_are_not_distinct_after_trim_and_lowercase()
    {
        Assert.Throws<ArgumentException>(() => NewFixed(["Hero", " hero ", "Villain"]));
    }

    [Fact]
    public void Choices_are_stored_trimmed_and_in_the_order_supplied_with_nothing_appended()
    {
        var q = NewFixed(["  Hero ", "Sidekick", " Villain  "]);

        Assert.Equal(["Hero", "Sidekick", "Villain"], q.FixedChoices);
    }

    [Fact]
    public void A_blank_prompt_is_rejected_for_Participants()
    {
        Assert.Throws<ArgumentException>(() => MatchingQuestion.Create("mq1", Language.En, "movies", "   ",
            MatchingAnswerSource.Participants, null, T0, source: QuestionSource.Admin, status: QuestionStatus.Pending));
    }

    [Fact]
    public void A_blank_prompt_is_rejected_for_Fixed()
    {
        Assert.Throws<ArgumentException>(() => MatchingQuestion.Create("mq1", Language.En, "movies", "   ",
            MatchingAnswerSource.Fixed, ["Hero", "Villain"], T0, source: QuestionSource.Admin, status: QuestionStatus.Pending));
    }

    [Fact]
    public void RecordServed_increments_TimesServed_by_one_per_call()
    {
        var q = NewParticipants();

        q.RecordServed();
        Assert.Equal(1, q.TimesServed);

        q.RecordServed();
        q.RecordServed();
        Assert.Equal(3, q.TimesServed);
    }

    [Fact]
    public void Topic_is_whatever_the_caller_supplies()
    {
        var q = MatchingQuestion.Create("mq1", Language.En, "movies", "Match each actor to their role.",
            MatchingAnswerSource.Participants, null, T0, source: QuestionSource.Admin, status: QuestionStatus.Pending,
            topic: "actor|role");

        Assert.Equal("actor|role", q.Topic);
    }

    [Fact]
    public void A_null_topic_is_valid_and_means_not_deduplicated()
    {
        var q = NewParticipants();

        Assert.Null(q.Topic);
    }

    [Theory]
    [InlineData(QuestionStatus.Approved, true)]
    [InlineData(QuestionStatus.Pending, false)]
    [InlineData(QuestionStatus.Rejected, false)]
    public void IsPlayable_is_true_only_when_approved(QuestionStatus status, bool expected)
    {
        var q = MatchingQuestion.Create("mq1", Language.En, "movies", "Match each actor to their role.",
            MatchingAnswerSource.Participants, null, T0, source: QuestionSource.Admin, status: status);

        Assert.Equal(expected, q.IsPlayable);
    }

    [Fact]
    public void SetStatus_allows_reverse_and_undeclared_values_and_only_approved_is_playable()
    {
        var q = NewParticipants();

        q.SetStatus(QuestionStatus.Approved);
        Assert.True(q.IsPlayable);

        q.SetStatus(QuestionStatus.Pending);
        Assert.False(q.IsPlayable);

        q.SetStatus((QuestionStatus)42);
        Assert.False(q.IsPlayable);
    }
}
