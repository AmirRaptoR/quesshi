using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The ring needs a correct arc the instant a reconnect lands, with no prior counted-down integer to
/// build on — only the server's absolute deadline and the clock skew measured at connect. See #13.
/// </summary>
public class RingClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_deadline_in_the_past_leaves_zero_remaining()
    {
        var endsAt = Now - TimeSpan.FromSeconds(5);

        Assert.Equal(0, RingClock.RemainingSeconds(endsAt, Now, TimeSpan.Zero));
    }

    [Fact]
    public void A_deadline_exactly_now_is_the_boundary_and_reads_zero()
    {
        Assert.Equal(0, RingClock.RemainingSeconds(Now, Now, TimeSpan.Zero));
    }

    [Fact]
    public void A_deadline_in_the_future_reads_the_seconds_between()
    {
        var endsAt = Now + TimeSpan.FromSeconds(12);

        Assert.Equal(12, RingClock.RemainingSeconds(endsAt, Now, TimeSpan.Zero));
    }

    /// <summary>A browser clock minutes out of true must still draw the same arc as the server sees.</summary>
    [Fact]
    public void Skew_shifts_the_remaining_time_by_the_measured_offset()
    {
        var endsAt = Now + TimeSpan.FromSeconds(12);

        // The client's clock reads 3s ahead of the server's, so the skew that corrects it is -3s:
        // true server time has only just reached "Now - 3s", leaving 3s more than the naive count.
        Assert.Equal(15, RingClock.RemainingSeconds(endsAt, Now, TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public void A_fractional_second_remaining_rounds_up_so_the_ring_never_shows_time_already_gone()
    {
        var endsAt = Now + TimeSpan.FromMilliseconds(500);

        Assert.Equal(1, RingClock.RemainingSeconds(endsAt, Now, TimeSpan.Zero));
    }
}
