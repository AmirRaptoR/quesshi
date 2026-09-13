using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class ProfileHistoryTests
{
    private static readonly PlayerSideDto Me = new("me", "Me", "firouzeh", 0, 0, 0, true);

    private static MatchSummaryDto Match(string id, string state, DateTimeOffset created,
        bool live = false, string? mode = null)
        => new(id, id, "en", state, Me, null, null, null, created, false, false,
            "draw", IsLive: live, Mode: mode);

    [Fact]
    public void Completed_history_excludes_active_games_sorts_newest_first_and_limits_rows()
    {
        var origin = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var matches = Enumerable.Range(0, 15)
            .Select(i => Match($"finished-{i}", "finished", origin.AddDays(i)))
            .Append(Match("active", "in_progress", origin.AddDays(20)));

        var result = ProfileHistory.Completed(matches);

        Assert.Equal(12, result.Count);
        Assert.Equal("finished-14", result[0].Id);
        Assert.DoesNotContain(result, match => match.Id == "active");
    }

    [Fact]
    public void History_routes_each_game_mode_to_its_playback_page()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal("/duel/async", ProfileHistory.Route(Match("async", "finished", now)));
        Assert.Equal("/live/live", ProfileHistory.Route(Match("live", "finished", now, live: true)));
        Assert.Equal("/matching/matching", ProfileHistory.Route(Match("matching", "finished", now, mode: "matching")));
    }
}
