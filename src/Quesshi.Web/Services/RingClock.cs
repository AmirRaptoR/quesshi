namespace Quesshi.Web.Services;

/// <summary>
/// Turns an absolute server deadline into a remaining-seconds count, so a reconnect mid-round has
/// something correct to draw immediately instead of waiting for a caller to hand it a counted-down
/// integer. <paramref name="skew"/> is the offset a client measures once at connect between its own
/// clock and the server's, so a browser clock minutes out of true still draws the right arc.
/// </summary>
public static class RingClock
{
    public static int RemainingSeconds(DateTimeOffset endsAt, DateTimeOffset now, TimeSpan skew)
    {
        var remaining = endsAt - (now + skew);
        return remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalSeconds);
    }
}
