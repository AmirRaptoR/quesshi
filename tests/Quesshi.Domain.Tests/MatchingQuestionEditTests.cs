using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchingQuestionEditTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddHours(1);

    private static MatchingQuestion New() => MatchingQuestion.Create("mq1", Language.En, "movies",
        "Match each actor to their role.", MatchingAnswerSource.Fixed, ["Hero", "Sidekick", "Villain"],
        QuestionSource.Admin, QuestionStatus.Pending, T0);

    [Fact]
    public void A_rejected_edit_throws_and_leaves_every_property_unchanged()
    {
        var q = New();

        Assert.Throws<ArgumentException>(() => q.Edit(Language.Fa, "sports", "New prompt?",
            MatchingAnswerSource.Fixed, ["only-one"], null, T1));

        Assert.Equal(Language.En, q.Lang);
        Assert.Equal("movies", q.MatchingCategoryId);
        Assert.Equal("Match each actor to their role.", q.Prompt);
        Assert.Equal(MatchingAnswerSource.Fixed, q.AnswerSource);
        Assert.Equal(["Hero", "Sidekick", "Villain"], q.FixedChoices);
        Assert.Equal(MediaRef.None, q.Media);
        Assert.Equal(T0, q.UpdatedAt);
    }

    [Fact]
    public void Editing_Fixed_to_Participants_empties_FixedChoices()
    {
        var q = New();

        q.Edit(Language.En, "movies", "Match each actor to their role.", MatchingAnswerSource.Participants,
            null, null, T1);

        Assert.Equal(MatchingAnswerSource.Participants, q.AnswerSource);
        Assert.Empty(q.FixedChoices);
    }

    [Fact]
    public void Editing_Participants_to_Fixed_requires_a_valid_choice_list()
    {
        var q = MatchingQuestion.Create("mq1", Language.En, "movies", "Match each actor to their role.",
            MatchingAnswerSource.Participants, null, QuestionSource.Admin, QuestionStatus.Pending, T0);

        Assert.Throws<ArgumentException>(() => q.Edit(Language.En, "movies", "Match each actor to their role.",
            MatchingAnswerSource.Fixed, null, null, T1));

        q.Edit(Language.En, "movies", "Match each actor to their role.", MatchingAnswerSource.Fixed,
            ["Hero", "Villain"], null, T1);

        Assert.Equal(MatchingAnswerSource.Fixed, q.AnswerSource);
        Assert.Equal(["Hero", "Villain"], q.FixedChoices);
    }
}
