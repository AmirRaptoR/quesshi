using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchingQuestionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static MatchingQuestion NewParticipants() => MatchingQuestion.Create("mq1", Language.En, "movies",
        "Match each actor to their role.", MatchingAnswerSource.Participants, null,
        QuestionSource.Admin, QuestionStatus.Pending, T0);

    private static MatchingQuestion NewFixed(IReadOnlyList<string>? choices = null) => MatchingQuestion.Create(
        "mq1", Language.En, "movies", "Match each actor to their role.", MatchingAnswerSource.Fixed,
        choices ?? ["Hero", "Sidekick", "Villain"], QuestionSource.Admin, QuestionStatus.Pending, T0);

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
            QuestionSource.Admin, QuestionStatus.Pending, T0));

        Assert.Equal("choices", ex.ParamName);
    }

    [Fact]
    public void Participants_with_no_choices_succeeds_with_empty_FixedChoices()
    {
        var q = NewParticipants();

        Assert.Empty(q.FixedChoices);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Fixed_succeeds_for_two_through_eight_choices(int count)
    {
        var choices = Enumerable.Range(1, count).Select(i => $"choice{i}").ToArray();

        var q = NewFixed(choices);

        Assert.Equal(count, q.FixedChoices.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void Fixed_rejects_out_of_range_choice_counts(int count)
    {
        var choices = Enumerable.Range(1, count).Select(i => $"choice{i}").ToArray();

        Assert.Throws<ArgumentException>(() => NewFixed(choices));
    }

    [Fact]
    public void Null_FixedChoices_is_valid_for_Participants()
    {
        var q = MatchingQuestion.Create("mq1", Language.En, "movies", "Match each actor to their role.",
            MatchingAnswerSource.Participants, null, QuestionSource.Admin, QuestionStatus.Pending, T0);

        Assert.Empty(q.FixedChoices);
    }

    [Fact]
    public void Null_FixedChoices_is_rejected_as_too_few_for_Fixed()
    {
        Assert.Throws<ArgumentException>(() => MatchingQuestion.Create("mq1", Language.En, "movies",
            "Match each actor to their role.", MatchingAnswerSource.Fixed, null,
            QuestionSource.Admin, QuestionStatus.Pending, T0));
    }
}
