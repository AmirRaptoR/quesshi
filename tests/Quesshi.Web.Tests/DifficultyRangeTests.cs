using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The two-handle difficulty slider (issue #89), which is only ever as good as the mapping between
/// where its handles are and what goes on the wire. The page is markup; this is the part that
/// decides anything, so this is the part with tests.
/// </summary>
public class DifficultyRangeTests
{
    [Fact]
    public void Nothing_stored_is_the_whole_ramp()
    {
        var range = DifficultyRange.From(null);

        Assert.True(range.IsWhole);
        Assert.Equal(1, range.Low);
        Assert.Equal(5, range.High);
    }

    [Fact]
    public void An_empty_list_is_the_whole_ramp_too()
        => Assert.True(DifficultyRange.From([]).IsWhole);

    /// <summary>The default the sheet opens on, and the one the home describes as "Mixed".</summary>
    [Fact]
    public void The_whole_ramp_is_every_level_and_travels_as_an_empty_list()
    {
        Assert.True(DifficultyRange.Whole.IsWhole);
        Assert.Equal([], DifficultyRange.Whole.Levels());
    }

    [Fact]
    public void All_five_stored_reads_back_as_the_whole_ramp()
        => Assert.True(DifficultyRange.From([1, 2, 3, 4, 5]).IsWhole);

    [Fact]
    public void A_narrowed_range_is_every_level_between_its_handles()
        => Assert.Equal([2, 3, 4], new DifficultyRange(2, 4).Levels());

    [Fact]
    public void Both_handles_on_one_level_is_that_level_alone()
        => Assert.Equal([3], new DifficultyRange(3, 3).Levels());

    [Fact]
    public void Each_end_of_the_track_is_a_range_of_one()
    {
        Assert.Equal([1], new DifficultyRange(1, 1).Levels());
        Assert.Equal([5], new DifficultyRange(5, 5).Levels());
    }

    /// <summary>Something short of the whole ramp at one end only: the commonest actual narrowing,
    /// and the one an off-by-one in <c>Levels()</c> would show up in.</summary>
    [Fact]
    public void A_range_open_at_one_end_still_carries_that_end()
    {
        Assert.Equal([1, 2, 3], new DifficultyRange(1, 3).Levels());
        Assert.Equal([3, 4, 5], new DifficultyRange(3, 5).Levels());
    }

    /// <summary>A build from before this issue could put any subset at all into localStorage, holes
    /// included. Widening to the range that covers it keeps that player's choice recognisable rather
    /// than discarding it — and is exactly what the home's summary line already said about it.</summary>
    [Fact]
    public void A_stored_set_with_a_hole_in_it_widens_to_its_ends()
    {
        var range = DifficultyRange.From([1, 4]);

        Assert.Equal(1, range.Low);
        Assert.Equal(4, range.High);
    }

    [Fact]
    public void Stored_levels_outside_the_ramp_are_dropped()
    {
        var range = DifficultyRange.From([0, 3, 9]);

        Assert.Equal(3, range.Low);
        Assert.Equal(3, range.High);
    }

    [Fact]
    public void Stored_levels_arrive_unsorted_and_are_still_read_end_to_end()
    {
        var range = DifficultyRange.From([4, 2, 3]);

        Assert.Equal(2, range.Low);
        Assert.Equal(4, range.High);
    }

    [Fact]
    public void Only_out_of_range_levels_leaves_the_whole_ramp()
        => Assert.True(DifficultyRange.From([0, 7]).IsWhole);

    // --- the handles themselves -------------------------------------------------------------------

    [Fact]
    public void Moving_a_handle_within_the_range_moves_only_that_handle()
    {
        Assert.Equal(new DifficultyRange(2, 5), DifficultyRange.Whole.WithLow(2));
        Assert.Equal(new DifficultyRange(1, 4), DifficultyRange.Whole.WithHigh(4));
    }

    /// <summary>The invariant the whole control rests on: whatever either handle is told, the easy
    /// end is never above the hard one.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void The_handles_can_never_cross(int moved)
    {
        var start = new DifficultyRange(2, 4);

        Assert.True(start.WithLow(moved).Low <= start.WithLow(moved).High);
        Assert.True(start.WithHigh(moved).Low <= start.WithHigh(moved).High);
    }

    [Fact]
    public void The_easy_handle_pushes_the_hard_one_rather_than_crossing_it()
        => Assert.Equal(new DifficultyRange(5, 5), new DifficultyRange(2, 3).WithLow(5));

    [Fact]
    public void The_hard_handle_pushes_the_easy_one_rather_than_crossing_it()
        => Assert.Equal(new DifficultyRange(1, 1), new DifficultyRange(3, 4).WithHigh(1));

    /// <summary>They may sit on the same level — "only the medium questions" is a real answer, and
    /// the pair collapsing onto one spot is how you say it.</summary>
    [Fact]
    public void The_handles_may_meet()
    {
        Assert.Equal(new DifficultyRange(3, 3), new DifficultyRange(1, 3).WithLow(3));
        Assert.Equal(new DifficultyRange(3, 3), new DifficultyRange(3, 5).WithHigh(3));
    }

    /// <summary>A value from outside the track — a hand-edited URL, a browser with its own idea of
    /// what a range input's bounds are — lands on the nearest level rather than off the end.</summary>
    [Theory]
    [InlineData(-3, 1)]
    [InlineData(0, 1)]
    [InlineData(6, 5)]
    [InlineData(99, 5)]
    public void A_handle_told_something_off_the_track_lands_on_the_nearest_level(int told, int expected)
    {
        Assert.Equal(expected, DifficultyRange.Whole.WithLow(told).Low);
        Assert.Equal(expected, DifficultyRange.Whole.WithHigh(told).High);
    }

    /// <summary>What the dimmed ends of the track are drawn from. The two ends must be exactly 0 and
    /// 100, or the rail would never quite reach its own handles.</summary>
    [Fact]
    public void A_handle_position_runs_the_full_width_of_the_track()
    {
        Assert.Equal(0, DifficultyRange.Position(1));
        Assert.Equal(50, DifficultyRange.Position(3));
        Assert.Equal(100, DifficultyRange.Position(5));
    }

    /// <summary>A round trip through the wire shape, which is what actually happens between one duel
    /// and the next: the sheet writes levels, localStorage keeps them, the sheet reopens on them.</summary>
    [Theory]
    [InlineData(1, 5)]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    [InlineData(3, 3)]
    [InlineData(4, 5)]
    public void A_range_survives_being_written_out_and_read_back(int low, int high)
    {
        var range = new DifficultyRange(low, high);

        Assert.Equal(range, DifficultyRange.From(range.Levels()));
    }
}
