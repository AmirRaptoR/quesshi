using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// Covers the new <c>/api/admin</c> routes this issue adds: authorisation, <c>/admin/live</c>'s
/// list, End duel's no-contest guarantee, and the kill switch's admin-facing toggle. Driven through
/// the real HTTP pipeline via <see cref="AdminApiTestHost"/>, the same shape as
/// <see cref="LiveEndpointsGuestGateTests"/> — <c>WebApplicationFactory</c> is unusable here too.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class AdminLiveEndpointsTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly AdminApiTestHost _host = new(fixture.Cluster);
    private static int _n;
    private const string Category = "ale-category";

    static AdminLiveEndpointsTests()
    {
        if (LiveShared.Categories.Items.All(c => c.Id != Category))
            LiveShared.Categories.Items.Add(new Category(Category, "ادمین", "Admin", "shield", "#663399"));

        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"ale-pool-q{slot}";
            if (LiveShared.Questions.Items.Any(q => q.Id == qid)) continue;
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, Category, MatchRules.LevelForSlot(slot),
                $"ale pool question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, DateTimeOffset.UtcNow,
                status: QuestionStatus.Approved));
        }
    }

    private static List<string> QuestionIds() => [.. Enumerable.Range(0, MatchRules.QuestionsPerMatch).Select(i => $"ale-pool-q{i}")];

    private Player NewPlayer(string prefix)
    {
        var id = $"ale-{prefix}-{Interlocked.Increment(ref _n)}";
        var player = Player.Register(id, $"{id}@example.com", id, Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(player);
        return player;
    }

    private HttpClient AdminClient()
    {
        var client = _host.NewClient();
        var admin = AdminUser.Create($"ale-admin-{Interlocked.Increment(ref _n)}", "admin", "admin@example.com", "hash", DateTimeOffset.UtcNow);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.AdminTokenIssuer.Issue(admin));
        return client;
    }

    private HttpClient PlayerClient(Player player)
    {
        var client = _host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.PlayerTokenIssuer.Issue(player));
        return client;
    }

    /// <summary>A token signed with the *player* key but shaped exactly like an admin token — the
    /// second half of the access criterion: rejected for the key, not merely for missing a claim.</summary>
    private string ForgedAdminLikeToken()
    {
        var forger = new AdminTokenIssuer(new AdminAuthOptions { Key = AdminApiTestHost.PlayerSigningKey, Issuer = "quesshi" });
        var admin = AdminUser.Create("forger", "forger", "forger@example.com", "hash", DateTimeOffset.UtcNow);
        return forger.Issue(admin);
    }

    // ---- Access ----

    public static IEnumerable<object[]> ProtectedRoutes()
    {
        yield return [HttpMethod.Get, "/api/admin/live"];
        yield return [HttpMethod.Post, "/api/admin/live/some-id/end"];
        yield return [HttpMethod.Post, "/api/admin/live/enabled?value=true"];
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task A_player_token_is_rejected_with_401(HttpMethod method, string path)
    {
        using var client = PlayerClient(NewPlayer("auth"));
        var response = await client.SendAsync(new HttpRequestMessage(method, path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task A_token_signed_with_Jwt_Key_is_rejected_with_401_even_shaped_like_an_admin_token(HttpMethod method, string path)
    {
        using var client = _host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ForgedAdminLikeToken());
        var response = await client.SendAsync(new HttpRequestMessage(method, path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task No_token_at_all_is_rejected_with_401(HttpMethod method, string path)
    {
        using var client = _host.NewClient();
        var response = await client.SendAsync(new HttpRequestMessage(method, path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- /admin/live ----

    [Fact]
    public async Task Lists_only_rows_currently_in_the_directory()
    {
        LiveShared.Directory.Rows.Clear();
        var challenger = NewPlayer("chal");
        var opponent = NewPlayer("opp");
        LiveShared.Directory.Rows["row-1"] = new LiveDirectoryRow("row-1", "ABC123", challenger.Id, opponent.Id,
            (int)Language.En, 2, 10, (int)LivePhase.Question, DateTimeOffset.UtcNow);

        using var client = AdminClient();
        var page = await client.GetFromJsonAsync<AdminLivePageDto>("/api/admin/live");

        Assert.Single(page!.Items);
        var row = page.Items[0];
        Assert.Equal("row-1", row.Id);
        Assert.Equal(challenger.DisplayName, row.ChallengerName);
        Assert.Equal(opponent.DisplayName, row.OpponentName);
        Assert.Equal("question", row.Phase);
        Assert.Equal(2, row.RoundIndex);
        Assert.Equal(10, row.TotalRounds);

        LiveShared.Directory.Rows.Clear();
    }

    [Fact]
    public async Task Reads_only_the_directory_no_Mongo_query_beyond_display_name_lookups()
    {
        LiveShared.Directory.Rows.Clear();
        var challenger = NewPlayer("noquery");
        LiveShared.Directory.Rows["row-nq"] = new LiveDirectoryRow("row-nq", "NOQ001", challenger.Id, null,
            (int)Language.En, 0, 10, (int)LivePhase.Lobby, DateTimeOffset.UtcNow);
        LiveShared.Archive.ResetCounters();

        using var client = AdminClient();
        await client.GetFromJsonAsync<AdminLivePageDto>("/api/admin/live");

        // ForPlayerAsync is the only query FakeArchive counts; zero proves the endpoint never
        // touched IMatchArchive at all, only the index and the player-name lookup.
        Assert.Equal(0, LiveShared.Archive.Queries);

        LiveShared.Directory.Rows.Clear();
    }

    [Fact]
    public async Task A_lobby_with_no_opponent_shows_an_empty_opponent_not_a_blank_row()
    {
        LiveShared.Directory.Rows.Clear();
        var challenger = NewPlayer("solo");
        LiveShared.Directory.Rows["row-2"] = new LiveDirectoryRow("row-2", "SOLO12", challenger.Id, null,
            (int)Language.En, 0, 10, (int)LivePhase.Lobby, DateTimeOffset.UtcNow);

        using var client = AdminClient();
        var page = await client.GetFromJsonAsync<AdminLivePageDto>("/api/admin/live");

        var row = Assert.Single(page!.Items);
        Assert.Equal(challenger.DisplayName, row.ChallengerName);
        Assert.Equal("", row.OpponentName);

        LiveShared.Directory.Rows.Clear();
    }

    [Fact]
    public async Task Rows_are_ordered_oldest_first()
    {
        LiveShared.Directory.Rows.Clear();
        var older = DateTimeOffset.UtcNow.AddMinutes(-5);
        var newer = DateTimeOffset.UtcNow;
        LiveShared.Directory.Rows["row-newer"] = new LiveDirectoryRow("row-newer", "NEW001", "p1", "p2", (int)Language.En, 0, 10, (int)LivePhase.Lobby, newer);
        LiveShared.Directory.Rows["row-older"] = new LiveDirectoryRow("row-older", "OLD001", "p1", "p2", (int)Language.En, 0, 10, (int)LivePhase.Lobby, older);

        using var client = AdminClient();
        var page = await client.GetFromJsonAsync<AdminLivePageDto>("/api/admin/live");

        Assert.Equal(["row-older", "row-newer"], page!.Items.Select(r => r.Id));

        LiveShared.Directory.Rows.Clear();
    }

    [Fact]
    public async Task A_row_whose_duel_could_no_longer_be_running_is_dropped_and_removed_from_the_index()
    {
        LiveShared.Directory.Rows.Clear();
        var maxAge = LiveRules.LobbyExpires + (10 * (MatchRules.QuestionTime + LiveRules.RevealTime)) + TimeSpan.FromMinutes(1);
        var longDead = LiveShared.TimeProvider.GetUtcNow() - maxAge - TimeSpan.FromSeconds(1);
        LiveShared.Directory.Rows["row-ghost"] = new LiveDirectoryRow("row-ghost", "GHOST1", "p1", "p2", (int)Language.En, 0, 10, (int)LivePhase.Question, longDead);

        using var client = AdminClient();
        var page = await client.GetFromJsonAsync<AdminLivePageDto>("/api/admin/live");

        Assert.Empty(page!.Items);
        Assert.False(LiveShared.Directory.Rows.ContainsKey("row-ghost"));
    }

    // ---- End duel ----

    [Fact]
    public async Task Ending_a_duel_is_a_no_contest_and_leaves_both_players_stats_untouched()
    {
        var challenger = NewPlayer("stat-chal");
        var opponent = NewPlayer("stat-opp");
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(Guid.NewGuid().ToString("N"));
        await grain.CreateAsync("STATEND", (int)Language.En, challenger.Id, QuestionIds());
        await grain.JoinAsync(opponent.Id);

        var statsBeforeChallenger = LiveShared.Players.Items.First(p => p.Id == challenger.Id).Stats;
        var statsBeforeOpponent = LiveShared.Players.Items.First(p => p.Id == opponent.Id).Stats;

        using var client = AdminClient();
        var response = await client.PostAsync($"/api/admin/live/{grain.GetPrimaryKeyString()}/end", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(statsBeforeChallenger, LiveShared.Players.Items.First(p => p.Id == challenger.Id).Stats);
        Assert.Equal(statsBeforeOpponent, LiveShared.Players.Items.First(p => p.Id == opponent.Id).Stats);

        var view = await grain.GetAsync(challenger.Id);
        Assert.Equal((int)MatchState.NoContest, view!.State);
        Assert.Null(view.WinnerId);
        Assert.False(view.IsDraw);
    }

    [Fact]
    public async Task Ending_an_already_finished_duel_is_a_no_op()
    {
        var challenger = NewPlayer("done-chal");
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(Guid.NewGuid().ToString("N"));
        await grain.CreateAsync("DONEEND", (int)Language.En, challenger.Id, QuestionIds());
        await grain.CancelAsync(challenger.Id); // already over, as a no-contest, before admin ever touches it

        var viewBefore = await grain.GetAsync(challenger.Id);

        using var client = AdminClient();
        var response = await client.PostAsync($"/api/admin/live/{grain.GetPrimaryKeyString()}/end", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var viewAfter = await grain.GetAsync(challenger.Id);
        Assert.Equal(viewBefore!.EndedAt, viewAfter!.EndedAt);
        Assert.Equal(viewBefore.State, viewAfter.State);
    }

    [Fact]
    public async Task Ending_an_id_no_live_duel_ever_used_is_a_no_op_that_still_returns_200()
    {
        var unusedId = Guid.NewGuid().ToString("N");
        var archiveCountBefore = LiveShared.Archive.Items.Count;

        using var client = AdminClient();
        var response = await client.PostAsync($"/api/admin/live/{unusedId}/end", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(LiveShared.Directory.Rows.ContainsKey(unusedId));
        Assert.Equal(archiveCountBefore, LiveShared.Archive.Items.Count);
    }

    // ---- The kill switch ----

    [Fact]
    public async Task Admin_can_toggle_Live_Enabled_and_it_is_reflected_on_the_settings_grain()
    {
        var settings = fixture.Cluster.GrainFactory.GetGrain<ILiveSettingsGrain>(0);
        using var client = AdminClient();

        await client.PostAsync("/api/admin/live/enabled?value=false", null);
        Assert.False(await settings.IsEnabledAsync());

        await client.PostAsync("/api/admin/live/enabled?value=true", null);
        Assert.True(await settings.IsEnabledAsync());
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
