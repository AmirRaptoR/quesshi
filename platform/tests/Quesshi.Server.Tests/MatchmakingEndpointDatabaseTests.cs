using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Features.Features.Matchmaking;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class MatchmakingEndpointDatabaseTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;
    private Guid _firstCategory;
    private Guid _secondCategory;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;

        _host = new PlatformHttpFixture([new MatchmakingFeature()]);
        await _host.StartAsync();
        await SeedBankAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Same_language_and_question_count_pair_into_a_started_session_with_the_first_waiters_content()
    {
        var (second, secondId) = await Host.CreateSecondPlayerClientAsync();

        using var first = await Enqueue(Host.Player, 10, [_firstCategory], [1, 2]);
        using var other = await Enqueue(second, 10, [_secondCategory], [3]);
        var sessions = await SessionsAsync();

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, other.StatusCode));
        var session = Assert.Single(sessions);
        Assert.Equal(("live", "running", Host.PlayerId), (session.Format, session.Status, session.OwnerId));
        Assert.Equivalent(new[] { Host.PlayerId, secondId }, await ParticipantsAsync(session.Id));
        using var content = JsonDocument.Parse(session.Content);
        Assert.Equal(10, content.RootElement.GetProperty("Count").GetInt32());
        var parameters = content.RootElement.GetProperty("Parameters");
        Assert.Equal([_firstCategory], parameters.GetProperty("categories").EnumerateArray().Select(x => x.GetGuid()));
        Assert.Equal([1, 2], parameters.GetProperty("levels").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Empty(await EntriesAsync());
    }

    [SkippableFact]
    public async Task Different_question_counts_are_not_paired()
    {
        var (second, _) = await Host.CreateSecondPlayerClientAsync();

        using var first = await Enqueue(Host.Player, 10);
        using var other = await Enqueue(second, 20);

        Assert.Empty(await SessionsAsync());
        Assert.Equal(2, (await EntriesAsync()).Count);
    }

    [SkippableFact]
    public async Task The_scheduled_job_pairs_entries_nobody_attempted_to_pair_within_its_interval()
    {
        var (_, secondId) = await Host.CreateSecondPlayerClientAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), MatchmakingFeature.SweepInterval);
        foreach (var playerId in new[] { Host.PlayerId, secondId })
            await Host.ExecuteOwnerAsync("""
                insert into matchmaking_entries (tenant_id, player_id, format, match_key, settings)
                values (@tenantId, @playerId, 'live', 'k', '{"language":"en","questionCount":10,"categories":[],"levels":[]}')
                """, new { tenantId = Host.TenantId, playerId });

        var sessions = await WaitForAsync(async () => await SessionsAsync(), found => found.Count == 1, TimeSpan.FromSeconds(12));

        Assert.Single(sessions);
        Assert.Empty(await EntriesAsync());
    }

    [SkippableFact]
    public async Task A_hundred_concurrent_requests_from_fifty_players_make_exactly_twenty_five_sessions()
    {
        var clients = new List<(HttpClient Client, Guid PlayerId)> { (Host.Player, Host.PlayerId) };
        for (var i = 1; i < 50; i++) clients.Add(await Host.CreateSecondPlayerClientAsync());

        var responses = await Task.WhenAll(clients.SelectMany(c => new[] { c, c }).Select(c => Enqueue(c.Client, 10)));
        var statuses = responses.Select(response => response.StatusCode).ToArray();
        foreach (var response in responses) response.Dispose();
        var sessions = await WaitForAsync(async () => await SessionsAsync(), found => found.Count >= 25 && EntriesAsync().Result.Count == 0, TimeSpan.FromSeconds(40));

        Assert.All(statuses, status => Assert.Contains(status, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(25, sessions.Count);
        var seats = await Host.QueryOwnerAsync<Guid>("select player_id from session_participants where tenant_id = @tenantId", new { tenantId = Host.TenantId });
        Assert.Equal(50, seats.Count);
        Assert.Equal(50, seats.Distinct().Count());
        var lockViolations = await Host.QueryOwnerAsync<long>(
            "select count(*) from jobs where last_error like '%LockOrderViolation%'", new { });
        Assert.Equal(0, lockViolations.Single());
    }

    [SkippableFact]
    public async Task Delete_removes_the_callers_entry_for_that_format()
    {
        var (second, secondId) = await Host.CreateSecondPlayerClientAsync();
        using var mine = await Enqueue(Host.Player, 10);
        using var theirs = await Enqueue(second, 20);

        using var response = await Host.Player.DeleteAsync("/api/x/matchmaking/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([secondId], (await EntriesAsync()).Select(entry => entry.PlayerId));
    }

    [SkippableFact]
    public async Task Routes_are_not_found_while_the_capability_is_off()
    {
        await Host.SetCapabilityAsync("feature", "matchmaking", false);

        using var enqueue = await Enqueue(Host.Player, 10);
        using var leave = await Host.Player.DeleteAsync("/api/x/matchmaking/live");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (enqueue.StatusCode, leave.StatusCode));
        Assert.Empty(await EntriesAsync());
    }

    [SkippableFact]
    public async Task The_inbox_shows_looking_for_an_opponent_until_the_player_is_paired_or_leaves()
    {
        var (second, _) = await Host.CreateSecondPlayerClientAsync();
        Assert.False(await HasWaitingItemAsync(Host.Player));

        using var queued = await Enqueue(Host.Player, 10);
        var whileWaiting = await HasWaitingItemAsync(Host.Player);
        using var left = await Host.Player.DeleteAsync("/api/x/matchmaking/live");
        var afterLeaving = await HasWaitingItemAsync(Host.Player);
        using var again = await Enqueue(Host.Player, 10);
        using var other = await Enqueue(second, 10);
        var afterPairing = (await HasWaitingItemAsync(Host.Player), await HasWaitingItemAsync(second));

        Assert.Equal((true, false, (false, false)), (whileWaiting, afterLeaving, afterPairing));
    }

    [SkippableFact]
    public async Task Join_waiting_formats_and_invalid_settings_are_rejected()
    {
        using var duel = await Host.Player.PostAsJsonAsync("/api/x/matchmaking", new { format = "async_duel", settings = new { questionCount = 10 } });
        using var badCount = await Enqueue(Host.Player, 7);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (duel.StatusCode, badCount.StatusCode));
        Assert.Empty(await EntriesAsync());
    }

    [SkippableFact]
    public async Task A_player_in_a_running_duel_is_not_queued_again()
    {
        var (second, _) = await Host.CreateSecondPlayerClientAsync();
        using var first = await Enqueue(Host.Player, 10);
        using var other = await Enqueue(second, 10);

        using var again = await Enqueue(Host.Player, 10);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Empty(await EntriesAsync());
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("Platform host did not start.");
        }
    }

    private Task<HttpResponseMessage> Enqueue(HttpClient client, int questionCount, Guid[]? categories = null, int[]? levels = null) =>
        client.PostAsJsonAsync("/api/x/matchmaking", new { format = "live", settings = new { questionCount, categories, levels } });

    private async Task SeedBankAsync()
    {
        _firstCategory = Guid.NewGuid();
        _secondCategory = Guid.NewGuid();
        await Host.SetCapabilityAsync("format", "live", true);
        await Host.SetCapabilityAsync("source", "bank", true);
        await Host.SetCapabilityAsync("kind", "choice", true);
        await Host.ExecuteOwnerAsync("""
            update tenant_capabilities
            set settings = jsonb_build_object('categoriesByFamily', jsonb_build_object('trivia', jsonb_build_array(@first, @second)))
            where tenant_id = @tenantId and type = 'source' and key = 'bank';
            insert into categories(tenant_id, id, slug, names)
            values (@tenantId, @first, 'first', '{"en":"First"}'::jsonb), (@tenantId, @second, 'second', '{"en":"Second"}'::jsonb);
            """, new { tenantId = Host.TenantId, first = _firstCategory, second = _secondCategory });
        foreach (var category in new[] { _firstCategory, _secondCategory })
            for (var index = 0; index < 30; index++)
                await Host.ExecuteOwnerAsync("""
                    insert into questions(tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source)
                    values (@tenantId, gen_random_uuid(), 'choice', 1, 'trivia', 'en', @category, @level, @prompt,
                            '{"options":["a","b","c","d"],"correctIndex":0}'::jsonb, @prompt, 'approved', 'admin')
                    """, new { tenantId = Host.TenantId, category, level = index % 3 + 1, prompt = $"{category} {index}" });
    }

    private async Task<bool> HasWaitingItemAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/me/inbox");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Any(item => item.GetProperty("type").GetString() == MatchmakingFeature.WaitingItemType);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var value = await read();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            value = await read();
        }
        return value;
    }

    private async Task<IReadOnlyList<SessionRow>> SessionsAsync() => await Host.QueryOwnerAsync<SessionRow>("""
        select id as Id, format as Format, status as Status, owner_id as OwnerId, content::text as Content
        from sessions where tenant_id = @tenantId
        """, new { tenantId = Host.TenantId });

    private async Task<IReadOnlyList<Guid>> ParticipantsAsync(Guid sessionId) => await Host.QueryOwnerAsync<Guid>(
        "select player_id from session_participants where tenant_id = @tenantId and session_id = @sessionId",
        new { tenantId = Host.TenantId, sessionId });

    private async Task<IReadOnlyList<EntryRow>> EntriesAsync() => await Host.QueryOwnerAsync<EntryRow>(
        "select player_id as PlayerId, format as Format from matchmaking_entries where tenant_id = @tenantId",
        new { tenantId = Host.TenantId });

    private sealed record SessionRow(Guid Id, string Format, string Status, Guid OwnerId, string Content);

    private sealed record EntryRow(Guid PlayerId, string Format);
}
