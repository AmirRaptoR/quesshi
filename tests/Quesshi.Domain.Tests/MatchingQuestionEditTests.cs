using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchingQuestionEditTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddHours(1);

    private static MatchingQuestion New() => MatchingQuestion.Create("mq1", Language.En, "movies",
        "Match each actor to their role.", MatchingAnswerSource.Fixed, ["Hero", "Sidekick", "Villain"],
        T0, source: QuestionSource.Admin, status: QuestionStatus.Pending);

    [Fact]
    public void A_rejected_edit_throws_and_leaves_every_property_unchanged()
    {
        var q = New();

        Assert.Throws<ArgumentException>(() => q.Edit(Language.Fa, "sports", "New prompt?",
            MatchingAnswerSource.Fixed, ["only-one"], null, "new-topic", T1));

        Assert.Equal(Language.En, q.Lang);
        Assert.Equal("movies", q.MatchingCategoryId);
        Assert.Equal("Match each actor to their role.", q.Prompt);
        Assert.Equal(MatchingAnswerSource.Fixed, q.AnswerSource);
        Assert.Equal(["Hero", "Sidekick", "Villain"], q.FixedChoices);
        Assert.Equal(MediaRef.None, q.Media);
        Assert.Null(q.Topic);
        Assert.Equal(QuestionStatus.Pending, q.Status);
        Assert.Equal(QuestionSource.Admin, q.Source);
        Assert.Equal(T0, q.CreatedAt);
        Assert.Equal(0, q.TimesServed);
        Assert.Equal(T0, q.UpdatedAt);
    }

    [Fact]
    public void Editing_Fixed_to_Participants_empties_FixedChoices()
    {
        var q = New();

        q.Edit(Language.En, "movies", "Match each actor to their role.", MatchingAnswerSource.Participants,
            null, null, "actor|role", T1);

        Assert.Equal(MatchingAnswerSource.Participants, q.AnswerSource);
        Assert.Empty(q.FixedChoices);
    }

    [Fact]
    public void Editing_Participants_to_Fixed_requires_a_valid_choice_list()
    {
        var q = MatchingQuestion.Create("mq1", Language.En, "movies", "Match each actor to their role.",
            MatchingAnswerSource.Participants, null, T0, source: QuestionSource.Admin, status: QuestionStatus.Pending);

        Assert.Throws<ArgumentException>(() => q.Edit(Language.En, "movies", "Match each actor to their role.",
            MatchingAnswerSource.Fixed, null, null, null, T1));

        q.Edit(Language.En, "movies", "Match each actor to their role.", MatchingAnswerSource.Fixed,
            ["Hero", "Villain"], null, null, T1);

        Assert.Equal(MatchingAnswerSource.Fixed, q.AnswerSource);
        Assert.Equal(["Hero", "Villain"], q.FixedChoices);
    }

    [Fact]
    public void Create_sets_CreatedAt_and_UpdatedAt_to_the_supplied_now()
    {
        var q = New();

        Assert.Equal(T0, q.CreatedAt);
        Assert.Equal(T0, q.UpdatedAt);
    }

    [Fact]
    public void Edit_sets_UpdatedAt_and_never_touches_CreatedAt()
    {
        var q = New();

        q.Edit(Language.En, "movies", "Match each actor to their role.", MatchingAnswerSource.Fixed,
            ["Hero", "Villain"], null, null, T1);

        Assert.Equal(T0, q.CreatedAt);
        Assert.Equal(T1, q.UpdatedAt);
    }

    [Fact]
    public void SetStatus_and_RecordServed_change_neither_timestamp()
    {
        var q = New();

        q.SetStatus(QuestionStatus.Approved);
        q.RecordServed();

        Assert.Equal(T0, q.CreatedAt);
        Assert.Equal(T0, q.UpdatedAt);
    }

    [Fact]
    public void Edit_updates_topic_and_accepts_an_earlier_timestamp()
    {
        var q = New();
        var earlier = T0.AddHours(-1);

        q.Edit(Language.Fa, "sports", "New prompt?", MatchingAnswerSource.Fixed,
            ["One", "Two"], null, "new-topic", earlier);

        Assert.Equal(Language.Fa, q.Lang);
        Assert.Equal("sports", q.MatchingCategoryId);
        Assert.Equal("New prompt?", q.Prompt);
        Assert.Equal("new-topic", q.Topic);
        Assert.Equal(earlier, q.UpdatedAt);
        Assert.Equal(T0, q.CreatedAt);
    }

    [Fact]
    public void Edit_copies_the_choices_list()
    {
        var q = New();
        var choices = new List<string> { "One", "Two" };

        q.Edit(Language.En, "movies", "Prompt", MatchingAnswerSource.Fixed,
            choices, null, null, T1);

        choices[0] = "Changed";
        Assert.Equal(["One", "Two"], q.FixedChoices);
    }
}
