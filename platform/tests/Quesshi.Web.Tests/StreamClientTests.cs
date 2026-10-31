using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class StreamClientTests
{
    [Theory]
    [InlineData(0, 0, 250)]
    [InlineData(0, 1, 500)]
    [InlineData(3, 1, 4000)]
    [InlineData(40, 1, 30000)]
    [InlineData(40, 0, 15000)]
    public void Retry_delay_grows_exponentially_with_jitter_and_stays_capped(int attempt, double jitter, double milliseconds) =>
        Assert.Equal(milliseconds, StreamClient.RetryDelay(attempt, jitter).TotalMilliseconds);

    [Fact]
    public void Stream_path_carries_the_watched_session_only_when_there_is_one()
    {
        var session = Guid.NewGuid();
        Assert.Equal("/api/stream", StreamClient.PathFor(null));
        Assert.Equal($"/api/stream?session={session}", StreamClient.PathFor(session));
    }
}
