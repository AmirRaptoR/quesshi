using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class QuestionCorrectnessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static Question Choice() => Question.Create("q1", Language.En, "geography", Difficulty.Easy,
        "Which one?", ["a", "b", "c", "d"], 2, T0);

    private static Question Sorting() => Question.Create("q2", Language.En, "geography", Difficulty.Easy,
        "Order these by population, largest first.", ["China", "India", "USA", "Indonesia"], 0, T0,
        kind: QuestionKind.Sort);

    private static Question CountryMap() => Question.Create("q3", Language.En, "geography", Difficulty.Easy,
        "Where is Germany?", [], 0, T0, kind: QuestionKind.Map,
        target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Borders);

    // Amsterdam, 50 km of slack.
    private static Question CityMap() => Question.Create("q4", Language.En, "geography", Difficulty.Medium,
        "Where is Amsterdam?", [], 0, T0, kind: QuestionKind.Map,
        target: MapTarget.City(52.37, 4.9, 50), baseLayer: MapBaseLayer.Blank);

    private static Question Players() => Question.Create("q5", Language.En, "relationships", Difficulty.Easy,
        "Who does the most work at home?", [], 0, T0, kind: QuestionKind.Players);

    // ---- Choice: unchanged ----

    [Fact]
    public void A_choice_question_is_still_graded_by_index()
    {
        var q = Choice();

        Assert.True(q.IsCorrect(2));
        Assert.False(q.IsCorrect(0));
        Assert.False(q.IsCorrect(-1));
    }

    [Fact]
    public void A_choice_question_can_also_be_graded_through_the_one_door()
    {
        var q = Choice();

        Assert.True(q.IsCorrect("2"));
        Assert.False(q.IsCorrect("0"));
        Assert.False(q.IsCorrect("-1"));
        Assert.False(q.IsCorrect((string?)null));
        Assert.False(q.IsCorrect("two"));
    }

    // ---- Sort ----

    /// <summary>
    /// The stored answer is already in stored-index terms — the submission path inverts the served
    /// shuffle once, before storing — so correctness here is literally "is this the identity order",
    /// and this checker never needs the seed.
    /// </summary>
    [Theory]
    [InlineData("0,1,2,3", true)]
    [InlineData(" 0, 1, 2, 3 ", true)]
    [InlineData("1,0,2,3", false)]
    [InlineData("3,2,1,0", false)]
    [InlineData("0,1,2", false)]
    [InlineData("0,1,2,3,4", false)]
    [InlineData("0,0,0,0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("۰,۱,۲,۳", false)]
    public void A_sort_is_right_when_the_stored_order_is_the_identity(string? response, bool expected)
        => Assert.Equal(expected, Sorting().IsCorrect(response));

    [Fact]
    public void A_player_who_undid_the_shuffle_is_graded_correct()
    {
        // The whole round trip: the card serves the items shuffled, the player drags them back into
        // the stored order, submission normalises what they sent, and the question grades it.
        var q = Sorting();
        var order = SortOrder.For("m-1", 3, q.Choices.Count);
        var submitted = order.Inverse;

        var stored = SortOrder.FormatOrder(order.ToStoredOrder(submitted));

        Assert.Equal("0,1,2,3", stored);
        Assert.True(q.IsCorrect(stored));
    }

    [Fact]
    public void A_player_who_swapped_two_items_is_graded_wrong()
    {
        var q = Sorting();
        var order = SortOrder.For("m-1", 3, q.Choices.Count);
        var submitted = order.Inverse.ToArray();
        (submitted[0], submitted[1]) = (submitted[1], submitted[0]);

        Assert.False(q.IsCorrect(SortOrder.FormatOrder(order.ToStoredOrder(submitted))));
    }

    // ---- Map: country ----

    [Theory]
    [InlineData("DE", true)]
    [InlineData("de", true)]
    [InlineData("  De  ", true)]
    [InlineData("FR", false)]
    [InlineData("D", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_country_answer_is_its_code(string? response, bool expected)
        => Assert.Equal(expected, CountryMap().IsCorrect(response));

    [Fact]
    public void A_country_question_is_not_graded_by_coordinates()
        => Assert.False(CountryMap().IsCorrect("51.16,10.45"));

    // ---- Map: city ----

    [Fact]
    public void A_pin_on_the_city_itself_is_right()
        => Assert.True(CityMap().IsCorrect(Geo.Format(52.37, 4.9)));

    [Fact]
    public void A_pin_just_inside_the_radius_is_right()
        // 49.9 km due north of Amsterdam, against a 50 km tolerance.
        => Assert.True(CityMap().IsCorrect("52.818761,4.9"));

    [Fact]
    public void A_pin_just_outside_the_radius_is_wrong()
        // 50.1 km due north: two hundred metres further, and the other side of the line.
        => Assert.False(CityMap().IsCorrect("52.82056,4.9"));

    [Theory]
    [InlineData("48.8566,2.3522")]   // Paris, well outside
    [InlineData("DE")]               // the wrong shape of answer entirely
    [InlineData("52,37,4,9")]        // a decimal comma, which must fail rather than misparse
    [InlineData("NaN,NaN")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_wrong(string? response)
        => Assert.False(CityMap().IsCorrect(response));

    [Fact]
    public void A_map_answer_does_not_grade_a_sorting_question()
        => Assert.False(Sorting().IsCorrect("52.37,4.9"));

    // ---- Players: no answer is ever correct ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(-1)]
    public void A_players_question_is_never_correct_by_index(int choiceIndex)
        => Assert.False(Players().IsCorrect(choiceIndex));

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData(null)]
    public void A_players_question_is_never_correct_by_response(string? response)
        => Assert.False(Players().IsCorrect(response));
}
