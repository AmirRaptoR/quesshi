using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The one line that replaced the home's whole question form (issue #88). Only the half that decides
/// anything is tested — the wording lives in <see cref="DuelSettingsLine"/>, which needs the real
/// i18n tables and a browser to fetch them; what belongs here is that "nothing picked" and
/// "everything picked" both mean Mixed, and that a narrowed range is described by its ends.
/// </summary>
public class DuelSettingsSummaryTests
{
    private static DuelSettingsDto Settings(int questions = 10, List<int>? levels = null, List<string>? categories = null)
        => new("en", questions, categories ?? [], levels ?? []);

    [Fact]
    public void The_question_count_is_carried_through_unchanged()
        => Assert.Equal(30, DuelSettingsSummary.From(Settings(questions: 30)).QuestionCount);

    [Fact]
    public void No_level_picked_is_mixed()
    {
        var summary = DuelSettingsSummary.From(Settings(levels: []));

        Assert.True(summary.MixedDifficulty);
        Assert.Null(summary.LevelFrom);
        Assert.Null(summary.LevelTo);
    }

    /// <summary>Picking all five says exactly what picking none says; offering "Very easy–Very hard"
    /// as though it were a narrowing would be a distinction without a difference.</summary>
    [Fact]
    public void Every_level_picked_is_also_mixed()
        => Assert.True(DuelSettingsSummary.From(Settings(levels: [1, 2, 3, 4, 5])).MixedDifficulty);

    [Fact]
    public void One_level_reads_as_that_level_rather_than_a_range()
    {
        var summary = DuelSettingsSummary.From(Settings(levels: [3]));

        Assert.False(summary.MixedDifficulty);
        Assert.True(summary.OneLevel);
        Assert.Equal(3, summary.LevelFrom);
        Assert.Equal(3, summary.LevelTo);
    }

    [Fact]
    public void A_narrowed_choice_is_described_by_its_ends()
    {
        var summary = DuelSettingsSummary.From(Settings(levels: [2, 3, 4]));

        Assert.False(summary.OneLevel);
        Assert.Equal(2, summary.LevelFrom);
        Assert.Equal(4, summary.LevelTo);
    }

    /// <summary>Today's chips can still produce a set with a hole in it; issue #89's slider cannot.
    /// Either way the ends are the honest summary — a list nobody can read at a glance is not.</summary>
    [Fact]
    public void A_set_with_a_hole_in_it_is_still_described_by_its_ends()
    {
        var summary = DuelSettingsSummary.From(Settings(levels: [1, 5]));

        Assert.Equal(1, summary.LevelFrom);
        Assert.Equal(5, summary.LevelTo);
    }

    [Fact]
    public void Levels_arrive_unsorted_and_are_still_read_end_to_end()
    {
        var summary = DuelSettingsSummary.From(Settings(levels: [4, 2, 3, 2]));

        Assert.Equal(2, summary.LevelFrom);
        Assert.Equal(4, summary.LevelTo);
    }

    /// <summary>These lists cross the wire; a level with no name to render must not widen the range.</summary>
    [Fact]
    public void Levels_outside_the_ramp_are_dropped()
    {
        var summary = DuelSettingsSummary.From(Settings(levels: [0, 3, 9]));

        Assert.Equal(3, summary.LevelFrom);
        Assert.Equal(3, summary.LevelTo);
    }

    [Fact]
    public void No_topic_picked_means_all_topics()
    {
        var summary = DuelSettingsSummary.From(Settings(categories: []));

        Assert.True(summary.AllTopics);
        Assert.Equal(0, summary.TopicCount);
    }

    [Fact]
    public void Picked_topics_are_counted()
    {
        var summary = DuelSettingsSummary.From(Settings(categories: ["geo", "general", "science"]));

        Assert.False(summary.AllTopics);
        Assert.Equal(3, summary.TopicCount);
    }
}
