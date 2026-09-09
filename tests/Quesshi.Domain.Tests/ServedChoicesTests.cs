using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

/// <summary>
/// <see cref="Question.ServedChoices"/> is the single door every card builder goes through, so what
/// it does per kind is the contract the three of them inherit — including the half that is about
/// what a card must <i>not</i> say.
/// </summary>
public class ServedChoicesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static Question Choice() => Question.Create("c1", Language.En, "geo", Difficulty.Easy,
        "Which is the capital of France?", ["Paris", "Lyon", "Nice", "Brest"], 0, T0);

    private static Question Sort() => Question.Create("s1", Language.En, "geo", Difficulty.Easy,
        "Order these rivers by length, longest first.", ["Nile", "Amazon", "Yangtze", "Mississippi"], 0, T0,
        kind: QuestionKind.Sort);

    private static Question Map() => Question.Create("m1", Language.En, "geo", Difficulty.Easy,
        "Find Germany.", [], 0, T0, kind: QuestionKind.Map,
        target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Borders);

    [Fact]
    public void A_choice_question_serves_its_options_exactly_as_stored()
    {
        var question = Choice();
        Assert.Equal(question.Choices, question.ServedChoices("match-1", 0));
    }

    [Fact]
    public void A_sorting_question_serves_the_permutation_SortOrder_gives_for_that_match_and_slot()
    {
        var question = Sort();
        var expected = SortOrder.For("match-1", 3, question.Choices.Count).Shuffle(question.Choices);

        Assert.Equal(expected, question.ServedChoices("match-1", 3));
    }

    /// <summary>
    /// The point of the seed, stated as a test: the same round always looks the same, and a
    /// different round in the same duel does not inherit its arrangement.
    /// </summary>
    [Fact]
    public void The_served_order_is_stable_for_a_round_and_differs_between_rounds()
    {
        var question = Sort();

        Assert.Equal(question.ServedChoices("match-1", 0), question.ServedChoices("match-1", 0));
        Assert.NotEqual(question.ServedChoices("match-1", 0), question.ServedChoices("match-2", 0));
    }

    /// <summary>
    /// The redaction, at its source. A sort's stored order is its whole answer, so the served items
    /// have to be the same four strings in a genuinely different order — and this asserts the
    /// "different" half against the actual stored sequence rather than trusting the shuffle.
    /// </summary>
    [Fact]
    public void A_sorting_question_never_serves_its_items_in_the_stored_order()
    {
        var question = Sort();

        // Not every (match, slot) permutes; this test needs one that does, and finding it by asking
        // is honest about the fact that the identity permutation is a legal output of a shuffle.
        var slot = Enumerable.Range(0, 20).First(s => !SortOrder.For("match-1", s, 4).Served.SequenceEqual([0, 1, 2, 3]));

        var served = question.ServedChoices("match-1", slot);
        Assert.NotEqual(question.Choices, served);
        Assert.Equal([.. question.Choices.Order()], [.. served.Order()]);
    }

    [Fact]
    public void A_map_question_serves_no_choices_at_all()
    {
        Assert.Empty(Map().ServedChoices("match-1", 0));
    }
}
