using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The topics page's selection model (issue #89): which tiles a language is even allowed to show,
/// what a tap does to the picks, and what "Any topic" means — which is emphatically not "none".
/// </summary>
public class TopicPicksTests
{
    private static CategoryDto Category(string id, params string[] langs)
        => new(id, id, id, id, "🌍", "#2FBFB0", IsActive: true, SortOrder: 0, NameNl: id,
            Langs: langs.Length == 0 ? null : [.. langs]);

    private static readonly List<CategoryDto> Bank =
    [
        Category("geo", "fa", "en", "nl"),
        Category("history", "fa", "en"),
        Category("knm", "nl"),
        Category("science")
    ];

    // --- which topics a language may show ---------------------------------------------------------

    [Fact]
    public void Only_the_topics_this_language_can_be_played_in_are_offered()
        => Assert.Equal(["geo", "history", "science"], TopicPicks.Offered(Bank, "en").Select(c => c.Id));

    /// <summary>Dutch sees the Dutch-only bank and loses the topic only Persian and English have —
    /// which is the whole point of the rule: the grid is what this language can actually play.</summary>
    [Fact]
    public void A_language_with_its_own_bank_sees_it()
        => Assert.Equal(["geo", "knm", "science"], TopicPicks.Offered(Bank, "nl").Select(c => c.Id));

    /// <summary>Null <c>Langs</c> is "the server did not say", which the chips this replaces read as
    /// playable — a topic is not hidden because a count was missing.</summary>
    [Fact]
    public void A_topic_with_no_languages_listed_is_offered_to_everyone()
        => Assert.All(new[] { "fa", "en", "nl" },
            lang => Assert.Contains("science", TopicPicks.Offered(Bank, lang).Select(c => c.Id)));

    [Fact]
    public void A_retired_topic_is_never_offered()
    {
        var retired = Category("gone", "en") with { IsActive = false };

        Assert.DoesNotContain("gone", TopicPicks.Offered([.. Bank, retired], "en").Select(c => c.Id));
    }

    [Fact]
    public void No_categories_at_all_offers_nothing_rather_than_failing()
        => Assert.Empty(TopicPicks.Offered(null, "en"));

    // --- tapping tiles ----------------------------------------------------------------------------

    [Fact]
    public void Tapping_an_unpicked_topic_picks_it()
        => Assert.Equal(["geo"], TopicPicks.Toggle([], "geo"));

    [Fact]
    public void Tapping_a_picked_topic_puts_it_back()
        => Assert.Equal(["geo", "science"], TopicPicks.Toggle(["geo", "history", "science"], "history"));

    [Fact]
    public void Tapping_the_last_picked_topic_leaves_any_topic()
        => Assert.Empty(TopicPicks.Toggle(["geo"], "geo"));

    [Fact]
    public void Any_topic_clears_every_pick()
        => Assert.Empty(TopicPicks.Any());

    /// <summary>Nothing picked is what every create path already reads as "draw the topics
    /// yourself", so the two really are the same value and not merely similar ones.</summary>
    [Fact]
    public void Any_topic_is_the_empty_pick_the_api_already_understands()
        => Assert.Equal([], new DuelSettingsDto("en", 10, TopicPicks.Any(), []).CategoryIds);

    // --- keeping the picks honest -----------------------------------------------------------------

    /// <summary>Switching a duel to Dutch after picking a Persian-only topic must not leave a count
    /// with no tile behind it — and must not ask the server for a bucket it cannot fill.</summary>
    [Fact]
    public void Picks_the_new_language_cannot_play_are_dropped()
        => Assert.Equal(["geo"], TopicPicks.Prune(["geo", "knm"], TopicPicks.Offered(Bank, "en")));

    [Fact]
    public void Picks_the_language_can_play_survive_untouched()
        => Assert.Equal(["geo", "history"], TopicPicks.Prune(["geo", "history"], TopicPicks.Offered(Bank, "en")));

    /// <summary>An id from an older build, or a topic retired since it was picked: gone, not carried
    /// as an invisible pick nobody can see or undo.</summary>
    [Fact]
    public void A_pick_that_no_longer_exists_at_all_is_dropped()
        => Assert.Empty(TopicPicks.Prune(["retired-last-year"], TopicPicks.Offered(Bank, "en")));

    [Fact]
    public void Pruning_nothing_leaves_any_topic()
        => Assert.Empty(TopicPicks.Prune(null, TopicPicks.Offered(Bank, "en")));

    // --- what the summary row reads ---------------------------------------------------------------

    /// <summary>The row on the questions sheet lists names, and it lists them in the order the grid
    /// shows them: a list that reshuffled itself every time somebody re-picked a topic would look
    /// like a different answer each time.</summary>
    [Fact]
    public void The_chosen_topics_come_back_in_the_order_the_grid_shows_them()
        => Assert.Equal(["geo", "history"],
            TopicPicks.Chosen(TopicPicks.Offered(Bank, "en"), ["history", "geo"]).Select(c => c.Id));

    [Fact]
    public void A_pick_the_grid_is_not_showing_is_not_named_in_the_row()
        => Assert.Empty(TopicPicks.Chosen(TopicPicks.Offered(Bank, "en"), ["knm"]));

    [Fact]
    public void Nothing_picked_names_nothing()
        => Assert.Empty(TopicPicks.Chosen(TopicPicks.Offered(Bank, "en"), null));
}
