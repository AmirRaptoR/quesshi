using System.Globalization;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The client half of a sorting question's one agreement with the server: what the submitted string
/// means. <c>SortOrder.ToStoredOrder</c> documents the trap from the other side — <c>"2,0,3,1"</c>
/// is "the item you showed me third goes first", and the other reading of it is the inverse
/// permutation, which grades every answer backwards while a test written with the same misreading
/// passes. So the tests below spell out the arrangement in words as well as in indices, and check
/// the string against what a person dragging those rows would actually have done.
/// </summary>
public class SortOrderingTests
{
    /// <summary>Four served items, so a place and a served position are never accidentally equal.</summary>
    private static readonly string[] Served = ["Everest", "K2", "Kangchenjunga", "Lhotse"];

    private static string Arrangement(SortOrdering ordering)
        => string.Join(" | ", ordering.Placed.Select(p => Served[p]));

    [Fact]
    public void An_untouched_list_submits_the_order_the_card_served()
    {
        // Not a null answer, and not "no answer": a player who agrees with the arrangement they were
        // shown has answered, and the identity string is what says so.
        var ordering = SortOrdering.Served(4);

        Assert.Equal("0,1,2,3", ordering.Response);
        Assert.Equal("Everest | K2 | Kangchenjunga | Lhotse", Arrangement(ordering));
    }

    [Fact]
    public void The_response_lists_served_positions_in_the_order_they_were_placed()
    {
        // Build the arrangement the example in the spec describes — the item served third first,
        // then the first, then the fourth, then the second — and check that it submits "2,0,3,1"
        // rather than the inverse permutation "1,3,0,2".
        var ordering = SortOrdering.Served(4)
            .Move(2, 0)   // Kangchenjunga | Everest | K2       | Lhotse
            .Move(2, 3);  // Kangchenjunga | Everest | Lhotse   | K2

        Assert.Equal("Kangchenjunga | Everest | Lhotse | K2", Arrangement(ordering));
        Assert.Equal("2,0,3,1", ordering.Response);
    }

    [Fact]
    public void A_drag_takes_the_row_out_and_slides_the_rest_along()
    {
        // Dragging the top row to the bottom moves one row three places and every other row up one.
        // The alternative reading — a swap of the two ends — is what a naive implementation does,
        // and it is wrong for every drag of more than one place.
        var ordering = SortOrdering.Served(4).Move(0, 3);

        Assert.Equal("K2 | Kangchenjunga | Lhotse | Everest", Arrangement(ordering));
        Assert.Equal("1,2,3,0", ordering.Response);
    }

    [Fact]
    public void A_sequence_of_drags_composes()
    {
        var ordering = SortOrdering.Served(4)
            .Move(3, 0)   // Lhotse | Everest | K2     | Kangchenjunga
            .Move(2, 1)   // Lhotse | K2      | Everest | Kangchenjunga
            .Move(0, 3);  // K2     | Everest | Kangchenjunga | Lhotse

        Assert.Equal("K2 | Everest | Kangchenjunga | Lhotse", Arrangement(ordering));
        Assert.Equal("1,0,2,3", ordering.Response);
    }

    [Fact]
    public void A_move_to_where_the_row_already_is_changes_nothing()
    {
        var ordering = SortOrdering.Served(4);

        // Reference equality, not just equal contents: a pointermove that resolves to the row's own
        // place fires many times a second, and each one must be free.
        Assert.Same(ordering, ordering.Move(2, 2));
    }

    [Fact]
    public void A_destination_past_either_end_is_clamped_rather_than_rejected()
    {
        // A finger dragged off the bottom of the list means "put it last", which is the useful
        // reading; and a stray index from a pointer event that raced a re-render must never throw
        // on the answering path.
        var served = SortOrdering.Served(4);

        Assert.Equal("1,2,3,0", served.Move(0, 99).Response);
        Assert.Equal("3,0,1,2", served.Move(3, -99).Response);

        // A source that is not a row at all is the one case that is refused outright rather than
        // clamped: there is no row it could plausibly have meant.
        Assert.Same(served, served.Move(9, 0));
    }

    [Fact]
    public void The_arrow_keys_move_a_row_one_place_at_a_time()
    {
        // The keyboard path has to be able to reach any arrangement the drag can, or it is not a
        // path to an answer, only a gesture at one.
        var ordering = SortOrdering.Served(4)
            .MoveDown(0)  // K2 | Everest | Kangchenjunga | Lhotse
            .MoveUp(3)    // K2 | Everest | Lhotse | Kangchenjunga
            .MoveUp(2);   // K2 | Lhotse  | Everest | Kangchenjunga

        Assert.Equal("K2 | Lhotse | Everest | Kangchenjunga", Arrangement(ordering));
        Assert.Equal("1,3,0,2", ordering.Response);
    }

    [Fact]
    public void Arrowing_off_the_ends_is_a_no_op()
    {
        var ordering = SortOrdering.Served(4);

        Assert.Same(ordering, ordering.MoveUp(0));
        Assert.Same(ordering, ordering.MoveDown(3));
    }

    [Fact]
    public void Keyboard_and_pointer_reach_the_same_arrangement()
    {
        // One operation underneath both, which is what stops the two input paths from drifting: a
        // drag is a move to wherever the finger is, an arrow press is a move to the next place.
        var dragged = SortOrdering.Served(4).Move(0, 2);
        var typed = SortOrdering.Served(4).MoveDown(0).MoveDown(1);

        Assert.Equal(dragged.Response, typed.Response);
    }

    [Fact]
    public void The_response_is_ASCII_digits_even_on_a_Persian_thread()
    {
        // The whole app runs in Persian for most of its players and a Blazor client takes the
        // browser's culture. SortOrder.TryParseOrder on the server rejects Persian digits outright,
        // so a response formatted on a Persian thread would be refused — in one language only.
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");

            var response = SortOrdering.Served(4).Move(2, 0).Response;

            Assert.Equal("2,0,1,3", response);
            Assert.All(response, c => Assert.True(char.IsAscii(c), $"'{c}' is not ASCII."));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Every_reachable_arrangement_is_still_a_permutation()
    {
        // The server rejects anything that is not a permutation of 0..3, so no move may ever produce
        // one — including the out-of-range destinations a pointer event can genuinely deliver.
        foreach (var from in Enumerable.Range(0, 4))
        foreach (var to in Enumerable.Range(-1, 6))
        {
            var placed = SortOrdering.Served(4).Move(from, to).Placed;
            Assert.Equal([0, 1, 2, 3], placed.Order());
        }
    }
}

/// <summary>
/// The one part of a drag that depends on where things are on the screen. The geometry itself comes
/// from the browser; deciding what it means is here, so it can be tested without one.
/// </summary>
public class SortDragTests
{
    /// <summary>Four rows, 56 px apart, the first centred at 100 — a plausible phone layout.</summary>
    private static readonly double[] Rows = [100, 156, 212, 268];

    [Theory]
    [InlineData(100, 0)]
    [InlineData(156, 1)]
    [InlineData(212, 2)]
    [InlineData(268, 3)]
    public void A_pointer_on_a_row_centre_targets_that_row(double y, int expected)
        => Assert.Equal(expected, SortDrag.TargetPlace(Rows, y));

    [Fact]
    public void A_pointer_between_two_rows_targets_the_nearer_one()
    {
        Assert.Equal(0, SortDrag.TargetPlace(Rows, 120));
        Assert.Equal(1, SortDrag.TargetPlace(Rows, 140));
    }

    [Fact]
    public void A_pointer_exactly_between_two_rows_keeps_the_higher_one()
    {
        // A tie has to resolve the same way every time, or a finger held still on the boundary makes
        // the list flicker between two arrangements on every pointermove event. Both boundaries
        // resolve upward, so the rule is one rule and not two.
        Assert.Equal(0, SortDrag.TargetPlace(Rows, 128));
        Assert.Equal(1, SortDrag.TargetPlace(Rows, 184));
    }

    [Fact]
    public void A_pointer_dragged_off_either_end_targets_the_end_row()
    {
        // On a phone this is most drags: the finger leaves the list long before it lets go.
        Assert.Equal(0, SortDrag.TargetPlace(Rows, -900));
        Assert.Equal(3, SortDrag.TargetPlace(Rows, 9000));
    }

    [Fact]
    public void An_empty_list_has_nothing_to_drop_onto()
        => Assert.Equal(-1, SortDrag.TargetPlace([], 100));
}
