using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

/// <summary>
/// The validation table from the spec, cell by cell. The "must be null" cells get a test each,
/// because they are the ones a rule written from "what does this kind need?" would silently skip.
/// </summary>
public class QuestionKindTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Four = ["a", "b", "c", "d"];
    private static readonly IReadOnlySet<string> KnownCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DE", "NL", "IR" };

    private static Question Choice() => Question.Create("q1", Language.En, "geography", Difficulty.Easy,
        "Which one?", Four, 1, T0);

    private static Question Sort() => Question.Create("q2", Language.En, "geography", Difficulty.Easy,
        "Order these by population.", Four, 0, T0, kind: QuestionKind.Sort);

    private static Question Map(MapTarget? target = null, MapBaseLayer layer = MapBaseLayer.Borders) =>
        Question.Create("q3", Language.En, "geography", Difficulty.Easy, "Where is it?", [], 0, T0,
            kind: QuestionKind.Map, target: target ?? MapTarget.Country("DE"), baseLayer: layer);

    // ---- the discriminator itself ----

    [Fact]
    public void A_question_written_the_old_way_is_a_choice_question()
    {
        var q = Choice();

        Assert.Equal(QuestionKind.Choice, q.Kind);
        Assert.Null(q.Target);
        Assert.Null(q.BaseLayer);
    }

    [Fact]
    public void Choice_is_the_default_enum_value_so_stored_rows_need_no_backfill()
        => Assert.Equal(QuestionKind.Choice, default(QuestionKind));

    [Fact]
    public void A_restored_question_keeps_its_kind_and_its_target()
    {
        var target = MapTarget.City(52.37, 4.9, 50);
        var q = Question.Restore("q4", Language.Fa, "geography", Difficulty.Hard, "Where?", [], 0,
            MediaRef.None, null, QuestionStatus.Approved, QuestionSource.Ai, T0, 0, 0,
            kind: QuestionKind.Map, target: target, baseLayer: MapBaseLayer.Blank);

        Assert.Equal(QuestionKind.Map, q.Kind);
        Assert.Equal(target, q.Target);
        Assert.Equal(MapBaseLayer.Blank, q.BaseLayer);
    }

    // ---- Choice column ----

    [Fact]
    public void A_choice_question_still_needs_four_distinct_choices()
    {
        Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?", ["a", "b", "c"], 0, T0));
        Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?", ["a", "b", "c", "a"], 0, T0));
    }

    [Fact]
    public void A_choice_question_still_needs_an_index_in_range()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            Question.Create("q", Language.En, "g", Difficulty.Easy, "?", Four, 4, T0));

    [Fact]
    public void A_choice_question_cannot_carry_a_map_target()
        => Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            Four, 1, T0, target: MapTarget.Country("DE")));

    [Fact]
    public void A_choice_question_cannot_carry_a_base_layer()
        => Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            Four, 1, T0, baseLayer: MapBaseLayer.Borders));

    // ---- Sort column ----

    [Fact]
    public void A_sorting_question_stores_its_four_items_in_the_correct_order()
    {
        var q = Sort();

        Assert.Equal(QuestionKind.Sort, q.Kind);
        Assert.Equal(Four, q.Choices);
        Assert.Equal(0, q.CorrectIndex);
    }

    [Fact]
    public void A_sorting_question_needs_four_distinct_items()
    {
        Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            ["a", "b", "c"], 0, T0, kind: QuestionKind.Sort));
        Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            ["a", "b", "c", "A"], 0, T0, kind: QuestionKind.Sort));
    }

    [Fact]
    public void A_sorting_question_pins_its_correct_index_to_zero()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            Four, 2, T0, kind: QuestionKind.Sort));

    [Fact]
    public void A_sorting_question_cannot_carry_a_map_target()
        => Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            Four, 0, T0, kind: QuestionKind.Sort, target: MapTarget.City(52.37, 4.9, 50)));

    [Fact]
    public void A_sorting_question_cannot_carry_a_base_layer()
        => Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            Four, 0, T0, kind: QuestionKind.Sort, baseLayer: MapBaseLayer.Blank));

    // ---- Map column ----

    [Fact]
    public void A_map_question_has_a_target_a_layer_and_no_choices()
    {
        var q = Map();

        Assert.Equal(QuestionKind.Map, q.Kind);
        Assert.Empty(q.Choices);
        Assert.Equal(0, q.CorrectIndex);
        Assert.Equal("DE", q.Target!.CountryCode);
        Assert.Equal(MapBaseLayer.Borders, q.BaseLayer);
    }

    [Fact]
    public void A_map_question_cannot_carry_choices()
        => Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            Four, 0, T0, kind: QuestionKind.Map, target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Blank));

    [Fact]
    public void A_map_question_pins_its_correct_index_to_zero()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            [], 1, T0, kind: QuestionKind.Map, target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Blank));

    [Fact]
    public void A_map_question_needs_a_target()
        => Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            [], 0, T0, kind: QuestionKind.Map, baseLayer: MapBaseLayer.Blank));

    [Fact]
    public void A_map_question_needs_a_base_layer()
        => Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            [], 0, T0, kind: QuestionKind.Map, target: MapTarget.Country("DE")));

    [Fact]
    public void A_prompt_is_still_required_whatever_the_kind()
        => Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "  ",
            [], 0, T0, kind: QuestionKind.Map, target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Blank));

    // ---- the country-code hook (the data itself is the map asset's, not the domain's) ----

    [Fact]
    public void A_country_the_map_can_draw_is_accepted()
    {
        var q = Question.Create("q", Language.En, "g", Difficulty.Easy, "?", [], 0, T0,
            kind: QuestionKind.Map, target: MapTarget.Country("nl"), baseLayer: MapBaseLayer.Borders,
            knownCountryCodes: KnownCodes);

        Assert.Equal("NL", q.Target!.CountryCode);
    }

    [Fact]
    public void A_country_the_map_cannot_draw_is_rejected()
        => Assert.Throws<ArgumentException>(() => Question.Create("q", Language.En, "g", Difficulty.Easy, "?",
            [], 0, T0, kind: QuestionKind.Map, target: MapTarget.Country("ZZ"), baseLayer: MapBaseLayer.Borders,
            knownCountryCodes: KnownCodes));

    [Fact]
    public void Without_the_hook_the_code_is_checked_for_shape_only()
    {
        // The bundled SVG's code list belongs to the map asset. Until a caller lends it to the
        // domain, an unknown-but-well-formed code passes; a malformed one never does.
        var q = Question.Create("q", Language.En, "g", Difficulty.Easy, "?", [], 0, T0,
            kind: QuestionKind.Map, target: MapTarget.Country("ZZ"), baseLayer: MapBaseLayer.Borders);

        Assert.Equal("ZZ", q.Target!.CountryCode);
        Assert.Throws<ArgumentException>(() => MapTarget.Country("ZZZ"));
    }

    // ---- editing ----

    [Fact]
    public void Editing_a_choice_question_the_old_way_leaves_it_a_choice_question()
    {
        var q = Choice();
        q.Edit(Language.En, "geography", Difficulty.Easy, "New?", Four, 2, null, null);

        Assert.Equal(QuestionKind.Choice, q.Kind);
        Assert.Null(q.Target);
    }

    [Fact]
    public void Editing_turns_a_choice_question_into_a_map_one_and_clears_the_choices()
    {
        var q = Choice();
        q.Edit(Language.En, "geography", Difficulty.Easy, "Where is Germany?", [], 0, null, null,
            QuestionKind.Map, MapTarget.Country("DE"), MapBaseLayer.Borders, KnownCodes);

        Assert.Equal(QuestionKind.Map, q.Kind);
        Assert.Empty(q.Choices);
        Assert.Equal("DE", q.Target!.CountryCode);
    }

    [Fact]
    public void A_rejected_edit_changes_nothing()
    {
        var q = Sort();
        Assert.Throws<ArgumentException>(() => q.Edit(Language.En, "geography", Difficulty.Easy, "New?",
            Four, 0, null, null, QuestionKind.Sort, MapTarget.Country("DE"), null));

        Assert.Equal("Order these by population.", q.Prompt);
        Assert.Null(q.Target);
    }
}
