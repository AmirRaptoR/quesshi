using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class WatchedSessionTests
{
    private static readonly Guid Id = Guid.Parse("3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0001");

    [Fact]
    public void The_session_route_selects_its_session() =>
        Assert.Equal(Id, WatchedSession.From(new Uri($"https://t.example/s/{Id}")));

    [Fact]
    public void The_session_query_still_selects_its_session() =>
        Assert.Equal(Id, WatchedSession.From(new Uri($"https://t.example/?session={Id}")));

    [Theory]
    [InlineData("https://t.example/")]
    [InlineData("https://t.example/s/not-a-guid")]
    [InlineData("https://t.example/profile")]
    public void Other_locations_watch_nothing(string uri) => Assert.Null(WatchedSession.From(new Uri(uri)));
}
