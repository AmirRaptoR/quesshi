using System.Net;
using System.Net.Http.Json;
using Quesshi.Features.Leaderboard;
using Quesshi.Features.Stats;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class AdminSessionOperationsEndpointTests : IAsyncLifetime
{
    private const string SessionsPath = "/api/admin/session-operations";
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;
    private Guid _runningId;
    private Guid _lobbyId;
    private Guid _foreignId;

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The host was not started.");
        }
    }

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;

        _host = new PlatformHttpFixture([new StatsFeature(), new LeaderboardFeature()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("feature", "stats", true);
        await _host.SetCapabilityAsync("feature", "leaderboard", true);
        _runningId = await SeedSessionAsync(_host.TenantId, _host.PlayerId, "running", "live");
        _lobbyId = await SeedSessionAsync(_host.TenantId, _host.PlayerId, "lobby", "async_duel");
        await SeedSessionAsync(_host.TenantId, _host.PlayerId, "finished", "live");

        var otherTenantId = Guid.NewGuid();
        var otherPlayerId = Guid.NewGuid();
        await _host.ExecuteOwnerAsync(
            """
            insert into tenants (id, slug, name, default_language) values (@otherTenantId, @slug, 'Other tenant', 'en');
            insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@otherTenantId, @otherPlayerId, 'Other', 'other', 'en');
            """,
            new { otherTenantId, otherPlayerId, slug = $"sessions-{otherTenantId:N}" });
        _foreignId = await SeedSessionAsync(otherTenantId, otherPlayerId, "running", "live");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Moderator_lists_the_tenants_active_sessions_of_every_format()
    {
        var sessions = await ListAsync();

        Assert.Equal(
            new[] { (_runningId, "live", "running"), (_lobbyId, "async_duel", "lobby") }.OrderBy(static s => s.Item1),
            sessions.Select(static s => (s.Id, s.Format, s.Status)).OrderBy(static s => s.Id));
    }

    [SkippableFact]
    public async Task Moderator_does_not_receive_an_active_session_from_another_tenant()
    {
        var sessions = await ListAsync();

        Assert.DoesNotContain(sessions, session => session.Id == _foreignId);
    }

    [SkippableFact]
    public async Task Ending_a_running_session_persists_cancelled_with_reason_admin()
    {
        using var response = await Host.Moderator.PostAsync($"{SessionsPath}/{_runningId}/end", content: null);

        var row = Assert.Single(await SessionRowsAsync(_runningId));
        Assert.Equal((HttpStatusCode.OK, "cancelled", "admin"), (response.StatusCode, row.Status, row.EndReason));
    }

    [SkippableFact]
    public async Task Moderator_cannot_end_a_session_of_another_tenant()
    {
        using var response = await Host.Moderator.PostAsync($"{SessionsPath}/{_foreignId}/end", content: null);

        Assert.Equal((HttpStatusCode.NotFound, "running"), (response.StatusCode, Assert.Single(await SessionRowsAsync(_foreignId)).Status));
    }

    [SkippableFact]
    public async Task Admin_ended_session_creates_no_player_stats_and_no_leaderboard_entry()
    {
        var sessionId = await SeedSessionAsync(Host.TenantId, Host.PlayerId, "running", "live", scored: true, score: 120);

        using var response = await Host.Moderator.PostAsync($"{SessionsPath}/{sessionId}/end", content: null);
        response.EnsureSuccessStatusCode();
        await WaitForEventJobAsync(sessionId);

        Assert.Equal(
            (0L, 0L, 0L, 0L),
            (await CountAsync("player_results", sessionId), await CountAsync("player_stats", sessionId),
             await CountAsync("leaderboard_entries", sessionId), await CountAsync("leaderboard_scores", sessionId)));
    }

    [SkippableFact]
    public async Task Repeated_admin_end_leaves_the_terminal_session_unchanged()
    {
        using var first = await Host.Moderator.PostAsync($"{SessionsPath}/{_runningId}/end", content: null);
        var afterFirst = Assert.Single(await SessionRowsAsync(_runningId));

        using var second = await Host.Moderator.PostAsync($"{SessionsPath}/{_runningId}/end", content: null);
        var afterSecond = Assert.Single(await SessionRowsAsync(_runningId));

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        Assert.Equal(afterFirst, afterSecond);
    }

    [SkippableFact]
    public async Task Session_operation_routes_admit_a_moderator_and_deny_an_editor()
    {
        await RoleMatrix.ForModeratorOnlyOperation(Host).AssertAsync(client => client.GetAsync(SessionsPath));
        await RoleMatrix.ForModeratorOnlyOperation(Host).AssertAsync(async client =>
        {
            var sessionId = await SeedSessionAsync(Host.TenantId, Host.PlayerId, "running", "live");
            return await client.PostAsync($"{SessionsPath}/{sessionId}/end", content: null);
        });
    }

    private async Task<IReadOnlyList<ActiveSession>> ListAsync()
    {
        using var response = await Host.Moderator.GetAsync(SessionsPath);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ActiveSession>>()
            ?? throw new InvalidOperationException("Session operations endpoint returned no body.");
    }

    private async Task<Guid> SeedSessionAsync(Guid tenantId, Guid playerId, string status, string format, bool scored = false, int score = 0)
    {
        var sessionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync(
            """
            insert into sessions (tenant_id, id, code, format, owner_id, lang, status, scored, capacity, content, phase, slot, started_at, ended_at, end_reason)
            values (@tenantId, @sessionId, @code, @format, @playerId, 'en', @status, @scored, 2, '{}'::jsonb,
                    case when @status = 'lobby' then 'lobby' else '0' end, case when @status = 'lobby' then -1 else 0 end,
                    case when @status = 'lobby' then null else now() end,
                    case when @status = 'finished' then now() else null end,
                    case when @status = 'finished' then 'completed' else null end);
            insert into session_participants (tenant_id, session_id, player_id, seat, role, status, score, result)
            values (@tenantId, @sessionId, @playerId, 0, 'player', 'active', @score, '{"runBegan":true}'::jsonb);
            """,
            new { tenantId, sessionId, playerId, status, format, scored, score, code = sessionId.ToString("N")[..8] });
        return sessionId;
    }

    private Task<IReadOnlyList<SessionRow>> SessionRowsAsync(Guid sessionId) =>
        Host.QueryOwnerAsync<SessionRow>(
            "select status as Status, end_reason as EndReason, version as Version, ended_at as EndedAt from sessions where id = @sessionId",
            new { sessionId });

    private async Task<long> CountAsync(string table, Guid sessionId) =>
        Assert.Single(await Host.QueryOwnerAsync<long>(
            table == "player_stats"
                ? "select count(*) from player_stats where tenant_id = @tenantId and player_id = @playerId"
                : table == "leaderboard_scores"
                    ? "select count(*) from leaderboard_scores where tenant_id = @tenantId and player_id = @playerId"
                    : $"select count(*) from {table} where session_id = @sessionId",
            new { sessionId, tenantId = Host.TenantId, playerId = Host.PlayerId }));

    private async Task WaitForEventJobAsync(Guid sessionId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var states = await Host.QueryOwnerAsync<string>(
                "select status from jobs where kind = 'event' and payload->>'subjectId' = @subject",
                new { subject = sessionId.ToString() });
            if (states.Count > 0 && states.All(static state => state == "done")) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException("The SessionEnded event jobs did not finish.");
    }

    private sealed record ActiveSession(Guid Id, string Code, string Format, string Status, DateTimeOffset CreatedAt, long Players);

    private sealed record SessionRow(string Status, string? EndReason, long Version, DateTimeOffset? EndedAt);
}
