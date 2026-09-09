using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The state issue #89's two screens hand back and forth in the address bar: what the questions
/// sheet is holding, and where Done goes when it is pressed. Everything here is a pure function of a
/// URL and the last-saved settings, which is the whole reason the draft lives in the URL rather than
/// in <c>AppState</c> — see <see cref="QuestionsDraft"/>'s own remarks.
/// </summary>
public class QuestionsDraftTests
{
    private static readonly DuelSettingsDto Saved = new("en", 30, ["geo", "science"], [2, 3]);

    private const string Base = "https://quesshi.example/play/questions";

    [Fact]
    public void A_bare_arrival_opens_on_the_last_saved_settings()
    {
        var draft = QuestionsDraft.Read(Base, Saved);

        Assert.Equal("en", draft.Lang);
        Assert.Equal(30, draft.QuestionCount);
        Assert.Equal(new DifficultyRange(2, 3), draft.Levels);
        Assert.Equal(["geo", "science"], draft.Topics);
        Assert.Equal("/play", draft.Return);
    }

    [Fact]
    public void What_the_url_carries_wins_over_what_was_saved()
    {
        var draft = QuestionsDraft.Read($"{Base}?lang=nl&n=50&lo=1&hi=4&t=history&r=%2Fplay", Saved);

        Assert.Equal("nl", draft.Lang);
        Assert.Equal(50, draft.QuestionCount);
        Assert.Equal(new DifficultyRange(1, 4), draft.Levels);
        Assert.Equal(["history"], draft.Topics);
    }

    /// <summary>The round trip that actually happens: the sheet links to the topics page, the topics
    /// page links back, and nothing the player chose may be lost on either hop.</summary>
    [Fact]
    public void A_draft_survives_the_trip_to_the_topics_page_and_back()
    {
        var draft = new QuestionsDraft("nl", 100, new DifficultyRange(2, 4), ["a", "b", "c"], "/lobby/ABCDEF");

        var read = QuestionsDraft.Read("https://quesshi.example" + draft.LinkTo("/play/topics"), Saved);

        // Field by field rather than record equality: the topics are a list, and two lists holding
        // the same strings are still two different objects to a compiler-generated Equals.
        Assert.Equal(draft.Lang, read.Lang);
        Assert.Equal(draft.QuestionCount, read.QuestionCount);
        Assert.Equal(draft.Levels, read.Levels);
        Assert.Equal(draft.Topics, read.Topics);
        Assert.Equal(draft.Return, read.Return);
    }

    [Fact]
    public void A_lobby_the_sheet_was_opened_from_survives_the_same_trip()
    {
        var draft = QuestionsDraft.From(Saved, "/lobby/ABCDEF", "match-1", lobbyIsLive: true);

        var read = QuestionsDraft.Read("https://quesshi.example" + draft.LinkTo("/play/topics"), Saved);

        Assert.Equal("match-1", read.LobbyId);
        Assert.True(read.LobbyIsLive);
    }

    [Fact]
    public void A_draft_from_the_home_names_no_lobby()
    {
        var read = QuestionsDraft.Read("https://quesshi.example" + QuestionsDraft.From(Saved).LinkTo("/play/topics"), Saved);

        Assert.Null(read.LobbyId);
        Assert.False(read.LobbyIsLive);
    }

    /// <summary>"Any topic" is an answer, not the absence of one, so an empty topics parameter has to
    /// survive the trip as an empty list rather than falling back to what was saved.</summary>
    [Fact]
    public void Any_topic_survives_the_trip_as_any_topic()
    {
        var read = QuestionsDraft.Read($"{Base}?lang=en&n=10&lo=1&hi=5&t=&r=%2Fplay", Saved);

        Assert.Empty(read.Topics);
    }

    [Fact]
    public void The_whole_ramp_goes_back_to_the_api_as_an_empty_level_list()
    {
        var settings = new QuestionsDraft("en", 20, DifficultyRange.Whole, ["geo"], "/play").ToSettings();

        Assert.Equal([], settings.Levels);
        Assert.Equal(["geo"], settings.CategoryIds);
        Assert.Equal(20, settings.QuestionCount);
        Assert.Equal("en", settings.Lang);
    }

    [Fact]
    public void A_narrowed_ramp_goes_back_as_every_level_between_the_handles()
        => Assert.Equal([2, 3, 4],
            new QuestionsDraft("en", 20, new DifficultyRange(2, 4), [], "/play").ToSettings().Levels);

    // --- a URL is not to be trusted ---------------------------------------------------------------

    [Theory]
    [InlineData("?lang=klingon")]
    [InlineData("?lang=")]
    public void A_language_the_app_does_not_ship_falls_back_to_the_saved_one(string query)
        => Assert.Equal("en", QuestionsDraft.Read(Base + query, Saved).Lang);

    [Theory]
    [InlineData("?n=nonsense")]
    [InlineData("?n=0")]
    [InlineData("?n=-5")]
    public void An_unreadable_count_falls_back_to_the_saved_one(string query)
        => Assert.Equal(30, QuestionsDraft.Read(Base + query, Saved).QuestionCount);

    /// <summary>One end of a range says nothing about where the other one was; guessing would
    /// silently widen or narrow somebody's choice.</summary>
    [Theory]
    [InlineData("?lo=1")]
    [InlineData("?hi=5")]
    [InlineData("?lo=x&hi=y")]
    public void Half_a_range_falls_back_to_the_saved_one(string query)
        => Assert.Equal(new DifficultyRange(2, 3), QuestionsDraft.Read(Base + query, Saved).Levels);

    [Fact]
    public void A_range_from_a_url_is_still_held_to_the_track()
    {
        var draft = QuestionsDraft.Read($"{Base}?lo=-4&hi=99", Saved);

        Assert.Equal(DifficultyRange.Whole, draft.Levels);
    }

    [Fact]
    public void A_crossed_range_from_a_url_is_uncrossed_rather_than_refused()
        => Assert.True(QuestionsDraft.Read($"{Base}?lo=4&hi=2", Saved).Levels.Low
                       <= QuestionsDraft.Read($"{Base}?lo=4&hi=2", Saved).Levels.High);

    [Fact]
    public void A_return_inside_the_app_is_honoured()
        => Assert.Equal("/lobby/ABCDEF", QuestionsDraft.SafeReturn("/lobby/ABCDEF"));

    /// <summary>The return target arrives from a URL, so it is treated as one. A scheme-relative
    /// <c>//evil.example</c> is a whole other site wearing a path's clothes, and NavigateTo would
    /// happily go there — which would make this sheet an open redirect anybody could put in front of
    /// a player.</summary>
    [Theory]
    [InlineData("//evil.example/phish")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("play")]
    [InlineData("")]
    [InlineData(null)]
    public void A_return_that_leaves_the_app_is_refused_and_lands_on_the_home(string? target)
        => Assert.Equal("/play", QuestionsDraft.SafeReturn(target));

    [Fact]
    public void A_hostile_return_does_not_survive_being_carried_either()
        => Assert.Equal("/play",
            QuestionsDraft.Read($"{Base}?r=%2F%2Fevil.example", Saved).Return);
}
