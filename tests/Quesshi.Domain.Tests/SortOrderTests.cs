using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class SortOrderTests
{
    /// <summary>
    /// The golden test. These literals are the output of the pinned algorithm — SHA-256 of
    /// "matchId|slot", SplitMix64 over the first eight bytes, Fisher-Yates high to low — and they
    /// are here so that any change to it fails loudly rather than quietly regrading duels that are
    /// already in flight. A silo running the old code and one running the new would otherwise show
    /// the same round in two different arrangements.
    /// </summary>
    [Fact]
    public void The_served_order_is_pinned_for_fixed_inputs()
    {
        Assert.Equal(new[] { 2, 0, 3, 1 }, SortOrder.For("m-1", 3).Served);
        Assert.Equal(new[] { 1, 0, 3, 2 }, SortOrder.For("m-1", 0).Served);
        Assert.Equal(new[] { 1, 2, 3, 0 }, SortOrder.For("live-7", 2).Served);
    }

    [Fact]
    public void The_inverse_is_pinned_too()
    {
        Assert.Equal(new[] { 1, 3, 0, 2 }, SortOrder.For("m-1", 3).Inverse);
        Assert.Equal(new[] { 3, 0, 1, 2 }, SortOrder.For("live-7", 2).Inverse);
    }

    [Fact]
    public void The_same_round_gives_the_same_order_every_time_it_is_asked()
    {
        // Not a tautology given what this replaces: Random.Shared would differ per process, and
        // string.GetHashCode differs per process too, so a silo restart mid-duel would reshuffle a
        // round the player is already looking at.
        var first = SortOrder.For("m-42", 7);
        var second = SortOrder.For("m-42", 7);

        Assert.Equal(first.Served, second.Served);
        Assert.Equal(first.Inverse, second.Inverse);
    }

    [Fact]
    public void Different_slots_of_one_match_are_shuffled_differently()
        => Assert.NotEqual(SortOrder.For("m-1", 0).Served, SortOrder.For("m-1", 1).Served);

    [Fact]
    public void The_seed_cannot_be_confused_by_where_the_id_ends()
        => Assert.NotEqual(SortOrder.For("m1", 23).Served, SortOrder.For("m12", 3).Served);

    [Fact]
    public void The_inverse_undoes_the_permutation_both_ways()
    {
        for (var slot = 0; slot < 50; slot++)
        {
            var order = SortOrder.For($"match-{slot}", slot);

            for (var p = 0; p < order.Count; p++)
                Assert.Equal(p, order.ServedPositionOf(order.StoredIndexAt(p)));
            for (var stored = 0; stored < order.Count; stored++)
                Assert.Equal(stored, order.StoredIndexAt(order.ServedPositionOf(stored)));
        }
    }

    [Fact]
    public void Every_item_is_served_exactly_once()
    {
        for (var slot = 0; slot < 50; slot++)
        {
            var order = SortOrder.For("m-1", slot);
            Assert.True(SortOrder.IsPermutation(order.Served, MatchRules.ChoicesPerQuestion));
            Assert.True(SortOrder.IsPermutation(order.Inverse, MatchRules.ChoicesPerQuestion));
        }
    }

    [Fact]
    public void An_order_can_be_asked_for_any_count()
    {
        Assert.Equal(new[] { 0 }, SortOrder.For("m-1", 0, 1).Served);
        Assert.Equal(new[] { 3, 1, 5, 2, 7, 0, 4, 6 }, SortOrder.For("m-1", 0, 8).Served);
    }

    [Fact]
    public void A_shuffle_needs_a_match_a_slot_and_something_to_shuffle()
    {
        Assert.Throws<ArgumentException>(() => SortOrder.For("", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SortOrder.For("m-1", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SortOrder.For("m-1", 0, 0));
    }

    [Fact]
    public void The_items_are_laid_out_in_served_order()
    {
        // ("m-1", 3) serves [2, 0, 3, 1]: the item stored third is shown first.
        var order = SortOrder.For("m-1", 3);

        Assert.Equal(new[] { "c", "a", "d", "b" }, order.Shuffle(new[] { "a", "b", "c", "d" }));
    }

    [Fact]
    public void A_shuffle_refuses_a_list_of_the_wrong_length()
        => Assert.Throws<ArgumentException>(() => SortOrder.For("m-1", 3).Shuffle(new[] { "a", "b" }));

    /// <summary>
    /// "2,0,3,1" means "the item you showed me third goes first" — served positions in the order the
    /// player placed them. The opposite reading is the inverse permutation and grades every answer
    /// backwards, so this test states the direction rather than assuming it.
    /// </summary>
    [Fact]
    public void A_submission_is_read_as_served_positions_in_the_players_order()
    {
        // ("m-1", 3) serves [2, 0, 3, 1], so served position 1 holds stored item 0.
        var order = SortOrder.For("m-1", 3);

        Assert.Equal(new[] { 0, 2, 1, 3 }, order.ToStoredOrder([1, 0, 3, 2]));
    }

    [Fact]
    public void A_player_who_undoes_the_shuffle_has_answered_0123()
    {
        var order = SortOrder.For("m-1", 3);

        // Placing the items back in stored order is exactly submitting the inverse permutation, and
        // that has to normalise to the identity — which is what correctness then checks for.
        Assert.Equal(new[] { 0, 1, 2, 3 }, order.ToStoredOrder(order.Inverse));
    }

    [Fact]
    public void A_submission_that_is_not_a_permutation_is_refused()
    {
        var order = SortOrder.For("m-1", 3);

        Assert.Throws<ArgumentException>(() => order.ToStoredOrder([0, 0, 1, 2]));
        Assert.Throws<ArgumentException>(() => order.ToStoredOrder([0, 1, 2]));
        Assert.Throws<ArgumentException>(() => order.ToStoredOrder([0, 1, 2, 4]));
    }

    [Theory]
    [InlineData("0,1,2,3", true)]
    [InlineData("3,2,1,0", true)]
    [InlineData(" 0 , 1 , 2 , 3 ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0,1,2", false)]
    [InlineData("0,1,2,3,4", false)]
    [InlineData("0,1,1,2", false)]
    [InlineData("0,1,2,4", false)]
    [InlineData("-1,1,2,3", false)]
    [InlineData("a,b,c,d", false)]
    // Persian digits from a Persian browser: refused, never quietly misread.
    [InlineData("۰,۱,۲,۳", false)]
    public void An_order_string_is_only_read_if_it_is_a_permutation(string? response, bool expected)
        => Assert.Equal(expected, SortOrder.TryParseOrder(response, MatchRules.ChoicesPerQuestion, out _));

    [Fact]
    public void Only_the_stored_order_is_the_identity()
    {
        Assert.True(SortOrder.IsIdentity([0, 1, 2, 3]));
        Assert.False(SortOrder.IsIdentity([1, 0, 2, 3]));
        Assert.True(SortOrder.IsIdentity(SortOrder.For("m-1", 3).ToStoredOrder(SortOrder.For("m-1", 3).Inverse)));
    }

    [Fact]
    public void An_order_is_written_the_way_it_is_read()
    {
        var order = SortOrder.For("m-1", 3);
        var written = SortOrder.FormatOrder(order.Served);

        Assert.Equal("2,0,3,1", written);
        Assert.True(SortOrder.TryParseOrder(written, 4, out var parsed));
        Assert.Equal(order.Served, parsed);
    }
}
