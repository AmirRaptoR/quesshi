using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// What the reveal screens draw. The sorting half is where the wrong reading of a response would
/// show up as a reveal that quietly disagrees with the score the player was just given — the answer
/// marked wrong beside a list that looks right — so these tests name the items rather than only
/// their indices.
/// </summary>
public class AnswerRevealTests
{
    /// <summary>A question stored in its correct order, which for a sort is the answer.</summary>
    private static readonly List<string> Correct = ["Everest", "K2", "Kangchenjunga", "Lhotse"];

    [Fact]
    public void A_perfect_answer_reads_as_the_correct_order_with_every_place_right()
    {
        // A sorting answer is normalised into stored-index terms at submission, so a right answer is
        // literally the identity — that is the whole point of normalising, and this is what it looks
        // like from the reveal's side.
        var placed = AnswerReveal.PlacedItems(Correct, "0,1,2,3");
        var rows = AnswerReveal.SortRows(Correct, placed);

        Assert.Equal(Correct, placed);
        Assert.All(rows, row => Assert.True(row.Right));
        Assert.Equal(Correct, rows.Select(r => r.Placed));
    }

    [Fact]
    public void A_stored_response_names_the_items_it_places()
    {
        // "2,0,3,1" in stored-index terms: Kangchenjunga first, then Everest, then Lhotse, then K2.
        var placed = AnswerReveal.PlacedItems(Correct, "2,0,3,1");

        Assert.Equal(["Kangchenjunga", "Everest", "Lhotse", "K2"], placed);
    }

    [Fact]
    public void The_correct_order_shows_beside_the_submitted_one_place_by_place()
    {
        var rows = AnswerReveal.SortRows(Correct, AnswerReveal.PlacedItems(Correct, "1,0,2,3"));

        Assert.Equal(4, rows.Count);

        // The first two are swapped; the last two are where they belong. A reveal that coloured the
        // whole column one way would be telling the player less than the score already told them.
        Assert.Equal(("Everest", "K2", false), (rows[0].Correct, rows[0].Placed, rows[0].Right));
        Assert.Equal(("K2", "Everest", false), (rows[1].Correct, rows[1].Placed, rows[1].Right));
        Assert.True(rows[2].Right);
        Assert.True(rows[3].Right);
    }

    [Fact]
    public void An_unanswered_round_still_reveals_the_correct_order()
    {
        // Timed out: the reveal has an answer to teach and nothing to compare it against, which is
        // an empty column beside a full one and not an empty reveal.
        var rows = AnswerReveal.SortRows(Correct, AnswerReveal.PlacedItems(Correct, null));

        Assert.Equal(Correct, rows.Select(r => r.Correct));
        Assert.All(rows, row => Assert.Null(row.Placed));
        Assert.All(rows, row => Assert.False(row.Right));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0,1,2")]        // too short
    [InlineData("0,1,2,3,4")]    // too long
    [InlineData("0,1,1,3")]      // not a permutation
    [InlineData("0,1,2,9")]      // out of range
    [InlineData("۰,۱,۲,۳")]      // Persian digits, rejected rather than misread
    [InlineData("a,b,c,d")]
    public void An_unreadable_response_blanks_one_column_and_not_the_round(string response)
    {
        Assert.Null(AnswerReveal.PlacedItems(Correct, response));

        var rows = AnswerReveal.SortRows(Correct, AnswerReveal.PlacedItems(Correct, response));
        Assert.Equal(Correct, rows.Select(r => r.Correct));
    }

    [Fact]
    public void A_reveal_with_no_correct_order_draws_nothing()
    {
        // A Choice round that somehow reached this path, or a contract field that arrived null.
        Assert.Empty(AnswerReveal.SortRows(null, ["Everest"]));
        Assert.Empty(AnswerReveal.SortRows([], null));
        Assert.Null(AnswerReveal.PlacedItems(null, "0,1,2,3"));
    }

    [Fact]
    public void A_choice_answer_is_right_when_the_index_matches()
    {
        Assert.True(AnswerReveal.IsRight(kind: 0, correctIndex: 2, correctTarget: null, choiceIndex: 2, response: null));
        Assert.False(AnswerReveal.IsRight(kind: 0, correctIndex: 2, correctTarget: null, choiceIndex: 1, response: null));

        // An unanswered choice round: no verdict, which is exactly what the rosette drew before any
        // of this existed.
        Assert.Null(AnswerReveal.IsRight(kind: 0, correctIndex: 2, correctTarget: null, choiceIndex: null, response: null));
    }

    [Fact]
    public void A_players_answer_is_never_marked_right_or_wrong()
    {
        // Every field a Choice reveal would call "correct" — index 0, from Question.CorrectIndex
        // being pinned there — must not be read as one for this kind.
        Assert.Null(AnswerReveal.IsRight(kind: 3, correctIndex: 0, correctTarget: null, choiceIndex: 0, response: null));
        Assert.Null(AnswerReveal.IsRight(kind: 3, correctIndex: 0, correctTarget: null, choiceIndex: 1, response: null));
        Assert.Null(AnswerReveal.IsRight(kind: 3, correctIndex: 0, correctTarget: null, choiceIndex: null, response: null));
    }

    [Fact]
    public void A_choice_option_is_marked_correct_only_at_the_correct_index()
    {
        Assert.True(AnswerReveal.IsCorrectOption(kind: 0, index: 2, correctIndex: 2));
        Assert.False(AnswerReveal.IsCorrectOption(kind: 0, index: 1, correctIndex: 2));
    }

    [Fact]
    public void A_players_option_is_never_marked_correct_even_at_the_pinned_zero()
    {
        // CorrectIndex is only ever the 0 validation pins it to for this kind — comparing an option's
        // index against it would light up whoever sits first in the roster as though they were the
        // answer, which is exactly what this must not do.
        Assert.False(AnswerReveal.IsCorrectOption(kind: 3, index: 0, correctIndex: 0));
        Assert.False(AnswerReveal.IsCorrectOption(kind: 3, index: 1, correctIndex: 0));
    }

    [Fact]
    public void A_sorting_answer_is_right_when_its_stored_order_is_the_identity()
    {
        Assert.True(AnswerReveal.IsRight(1, 0, null, -1, "0,1,2,3"));
        Assert.False(AnswerReveal.IsRight(1, 0, null, -1, "0,1,3,2"));
        Assert.False(AnswerReveal.IsRight(1, 0, null, -1, "nonsense"));
        Assert.Null(AnswerReveal.IsRight(1, 0, null, -1, null));
    }

    [Fact]
    public void A_country_answer_is_right_when_the_codes_match()
    {
        Assert.True(AnswerReveal.IsRight(2, 0, "DE", -1, "de"));
        Assert.False(AnswerReveal.IsRight(2, 0, "DE", -1, "NL"));
        Assert.Null(AnswerReveal.IsRight(2, 0, "DE", -1, null));
    }

    [Fact]
    public void A_city_answer_has_no_verdict_this_side_can_give()
    {
        // Deliberate, and the one place this file admits to not knowing something: a city target is
        // judged against a tolerance radius, and no reveal contract carries the radius. Guessing
        // from the distance alone would mean showing a player a red mark for an answer the server
        // scored as right. Null is honest; the screens draw the target and the pin and let the
        // picture speak.
        Assert.Null(AnswerReveal.IsRight(2, 0, "52.37,4.9", -1, "52.36,4.91"));
        Assert.Null(AnswerReveal.IsRight(2, 0, "52.37,4.9", -1, "35.69,51.39"));
    }
}
