using Quesshi.Domain;
using Quesshi.Infrastructure.Mongo;

namespace Quesshi.Server.Tests;

/// <summary>
/// <see cref="QuestionDoc.From"/> and <see cref="QuestionDoc.ToDomain"/> round-tripping the kind,
/// the map target and the base layer for all three kinds. This needs no Mongo at all — it is plain
/// object mapping — so it runs everywhere, unlike <see cref="MongoQuestionKindTests"/>'s legacy-BSON
/// cases, which genuinely need a server to prove what a document with no <c>Kind</c> field deserialises
/// to.
/// </summary>
public class QuestionDocKindTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Four = ["a", "b", "c", "d"];

    [Fact]
    public void A_choice_question_round_trips_with_no_target_and_no_base_layer()
    {
        var original = Question.Create("q1", Language.En, "geography", Difficulty.Easy,
            "Which one?", Four, 1, T0);

        var restored = QuestionDoc.From(original).ToDomain();

        Assert.Equal(QuestionKind.Choice, restored.Kind);
        Assert.Null(restored.Target);
        Assert.Null(restored.BaseLayer);
        Assert.Equal(original.Choices, restored.Choices);
        Assert.Equal(original.CorrectIndex, restored.CorrectIndex);
    }

    [Fact]
    public void A_sort_question_round_trips_its_stored_order_with_no_target()
    {
        var original = Question.Create("q2", Language.En, "geography", Difficulty.Easy,
            "Order these by population.", Four, 0, T0, kind: QuestionKind.Sort);

        var restored = QuestionDoc.From(original).ToDomain();

        Assert.Equal(QuestionKind.Sort, restored.Kind);
        Assert.Null(restored.Target);
        Assert.Null(restored.BaseLayer);
        Assert.Equal(original.Choices, restored.Choices);
    }

    [Fact]
    public void A_map_question_with_a_country_target_round_trips_the_target_and_the_base_layer()
    {
        var original = Question.Create("q3", Language.En, "geography", Difficulty.Easy, "Where is it?", [], 0, T0,
            kind: QuestionKind.Map, target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Borders);

        var restored = QuestionDoc.From(original).ToDomain();

        Assert.Equal(QuestionKind.Map, restored.Kind);
        Assert.Equal(MapBaseLayer.Borders, restored.BaseLayer);
        Assert.NotNull(restored.Target);
        Assert.True(restored.Target!.IsCountry);
        Assert.Equal("DE", restored.Target.CountryCode);
        Assert.Null(restored.Target.Latitude);
        Assert.Null(restored.Target.Longitude);
        Assert.Null(restored.Target.RadiusKm);
    }

    [Fact]
    public void A_map_question_with_a_city_target_round_trips_the_point_and_radius()
    {
        var original = Question.Create("q4", Language.En, "geography", Difficulty.Easy, "Where is it?", [], 0, T0,
            kind: QuestionKind.Map, target: MapTarget.City(52.37, 4.9, 50), baseLayer: MapBaseLayer.Blank);

        var restored = QuestionDoc.From(original).ToDomain();

        Assert.Equal(QuestionKind.Map, restored.Kind);
        Assert.Equal(MapBaseLayer.Blank, restored.BaseLayer);
        Assert.NotNull(restored.Target);
        Assert.True(restored.Target!.IsCity);
        Assert.Null(restored.Target.CountryCode);
        Assert.Equal(52.37, restored.Target.Latitude);
        Assert.Equal(4.9, restored.Target.Longitude);
        Assert.Equal(50, restored.Target.RadiusKm);
    }
}
