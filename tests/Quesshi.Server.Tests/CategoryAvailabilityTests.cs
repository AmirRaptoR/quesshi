using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Server.Api;

namespace Quesshi.Server.Tests;

/// <summary>
/// A Persian profile could pick the Dutch-only KNM category and be handed ten Persian questions
/// about birds instead, because nothing told the lobby that the category was empty for that
/// language. This is what it is told.
/// </summary>
public class CategoryAvailabilityTests
{
    private static BucketCount Bucket(Language lang, string category, int approved, int pending = 0)
        => new(lang, category, Difficulty.Medium, approved, pending);

    [Fact]
    public void A_category_is_offered_only_in_languages_it_has_questions_in()
    {
        var playable = Mappers.PlayableLanguages([
            Bucket(Language.Nl, "knm", 202),
            Bucket(Language.Fa, "nature", 76),
            Bucket(Language.En, "nature", 54)
        ]);

        Assert.Equal(["nl"], playable["knm"]);
        Assert.Equal(["en", "fa"], playable["nature"]);
    }

    [Fact]
    public void A_category_with_only_pending_questions_is_not_offered()
    {
        var playable = Mappers.PlayableLanguages([Bucket(Language.Nl, "knm", approved: 0, pending: 40)]);

        Assert.False(playable.ContainsKey("knm"));
    }

    [Fact]
    public void Languages_are_listed_once_however_many_buckets_they_span()
    {
        var playable = Mappers.PlayableLanguages([
            new BucketCount(Language.Nl, "knm", Difficulty.VeryEasy, 40, 0),
            new BucketCount(Language.Nl, "knm", Difficulty.Hard, 40, 0),
            new BucketCount(Language.Nl, "knm", Difficulty.VeryHard, 40, 0)
        ]);

        Assert.Equal(["nl"], playable["knm"]);
    }

    [Fact]
    public void A_category_nobody_has_written_for_is_absent_rather_than_empty()
        => Assert.Empty(Mappers.PlayableLanguages([]));
}
