using System.Net;
using System.Net.Http.Json;
using Quesshi.Features.Features.Friends;
using Quesshi.Shared;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class SessionHistoryEndpointDatabaseTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new FriendsFeature()]);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    /// <summary>AC-2.</summary>
    [SkippableFact]
    public async Task Following_the_session_cursor_yields_the_next_page_without_repeating_a_session()
    {
        var (client, playerId) = await Host.CreateSecondPlayerClientAsync();
        var expected = new List<Guid>();
        // Seven sessions; the last two share one joined_at so the session id breaks the tie.
        for (var index = 0; index < 7; index++)
            expected.Add(await SeedSessionAsync(playerId, "finished", $"timestamptz '2026-01-0{Math.Min(index, 5) + 1} 10:00:00+00'", questions: 0));

        var first = await GetSessionsAsync(client, "/api/me/sessions?limit=3");
        var second = await GetSessionsAsync(client, $"/api/me/sessions?limit=3&before={Uri.EscapeDataString(first.Next!)}");
        var third = await GetSessionsAsync(client, $"/api/me/sessions?limit=3&before={Uri.EscapeDataString(second.Next!)}");

        Assert.Equal(3, first.Sessions.Count);
        Assert.Equal(3, second.Sessions.Count);
        Assert.Single(third.Sessions);
        Assert.Null(third.Next);
        var all = first.Sessions.Concat(second.Sessions).Concat(third.Sessions).Select(entry => entry.Id).ToArray();
        Assert.Equal(7, all.Distinct().Count());
        Assert.Equal(expected.OrderBy(id => id), all.OrderBy(id => id));
        var entries = first.Sessions.Concat(second.Sessions).Concat(third.Sessions).ToArray();
        Assert.Equal(entries.OrderByDescending(entry => entry.JoinedAt).ThenBy(entry => entry.Id), entries);
    }

    /// <summary>AC-3.</summary>
    [SkippableFact]
    public async Task A_session_the_same_player_id_holds_in_another_tenant_is_absent_from_the_history()
    {
        var (client, playerId) = await Host.CreateSecondPlayerClientAsync();
        var own = await SeedSessionAsync(playerId, "finished", "now()", questions: 0);
        var (_, otherPlayerId) = await Host.CreateOtherTenantPlayerWithIdAsync();
        var otherTenant = (await Host.QueryOwnerAsync<Guid>("select tenant_id from players where id = @id", new { id = otherPlayerId })).Single();
        // A plausible leak: the caller's own player id also exists as a seat in a newer session of the other tenant.
        var foreignSession = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into players(tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @player, 'Twin', 'twin', 'en');
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content, started_at, ended_at)
            values (@tenant, @session, 'FOREIGN1', 'poll', @player, 'en', 'finished', 4, '{}'::jsonb, now(), now());
            insert into session_participants(tenant_id, session_id, player_id, seat, joined_at)
            values (@tenant, @session, @player, 0, now() + interval '1 hour');
            """, new { tenant = otherTenant, player = playerId, session = foreignSession });

        var page = await GetSessionsAsync(client, "/api/me/sessions");

        Assert.Equal([own], page.Sessions.Select(entry => entry.Id));
    }

    /// <summary>AC-5.</summary>
    [SkippableFact]
    public async Task Following_the_question_cursor_yields_the_next_page_without_repeating_a_question()
    {
        var (client, playerId) = await Host.CreateSecondPlayerClientAsync();
        var sessionId = await SeedSessionAsync(playerId, "finished", "now()", questions: 5);

        var first = await GetQuestionsAsync(client, $"/api/sessions/{sessionId}/history?limit=2");
        var second = await GetQuestionsAsync(client, $"/api/sessions/{sessionId}/history?limit=2&after={first.Next}");
        var third = await GetQuestionsAsync(client, $"/api/sessions/{sessionId}/history?limit=2&after={second.Next}");

        Assert.Equal([0, 1], first.Items.Select(item => item.Slot));
        Assert.Equal([2, 3], second.Items.Select(item => item.Slot));
        Assert.Equal([4], third.Items.Select(item => item.Slot));
        Assert.Null(third.Next);
        Assert.Equal("Question 3", second.Items[1].Prompt);
        Assert.Equal(3, second.Items[1].Answers.Single().GetProperty("index").GetInt32());
    }

    /// <summary>AC-6.</summary>
    [SkippableFact]
    public async Task A_player_who_was_not_seated_cannot_read_the_question_history()
    {
        var (_, seated) = await Host.CreateSecondPlayerClientAsync();
        var (stranger, _) = await Host.CreateSecondPlayerClientAsync();
        var sessionId = await SeedSessionAsync(seated, "finished", "now()", questions: 2);

        using var response = await stranger.GetAsync($"/api/sessions/{sessionId}/history");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Question", await response.Content.ReadAsStringAsync());
    }

    /// <summary>AC-7.</summary>
    [SkippableFact]
    public async Task A_running_sessions_question_history_is_unavailable_even_to_a_seated_player()
    {
        var (client, playerId) = await Host.CreateSecondPlayerClientAsync();
        var sessionId = await SeedSessionAsync(playerId, "running", "now()", questions: 2);

        using var response = await client.GetAsync($"/api/sessions/{sessionId}/history");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("Question", await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task A_malformed_cursor_is_rejected()
    {
        using var response = await Host.Player.GetAsync("/api/me/sessions?before=garbage");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("Platform host did not start.");
        }
    }

    private static async Task<SessionHistoryPage> GetSessionsAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionHistoryPage>())!;
    }

    private static async Task<SessionQuestionHistoryPage> GetQuestionsAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionQuestionHistoryPage>())!;
    }

    private async Task<Guid> SeedSessionAsync(Guid playerId, string status, string joinedAtSql, int questions)
    {
        var sessionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync($$"""
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content, started_at, ended_at)
            values (@tenantId, @sessionId, @code, 'poll', @playerId, 'en', @status, 4, '{}'::jsonb, now(),
                    case when @status = 'finished' then now() end);
            insert into session_participants(tenant_id, session_id, player_id, seat, joined_at)
            values (@tenantId, @sessionId, @playerId, 0, {{joinedAtSql}});
            """, new { tenantId = Host.TenantId, sessionId, code = $"H{sessionId:N}"[..12], playerId, status });
        for (var slot = 0; slot < questions; slot++)
        {
            await Host.ExecuteOwnerAsync("""
                insert into session_items(tenant_id, session_id, slot, kind, kind_version, prompt, payload, card, reveal)
                values (@tenantId, @sessionId, @slot, 'choice', 1, @prompt, '{}'::jsonb, '{"options":["a","b"]}'::jsonb, '{"correctIndex":0}'::jsonb);
                insert into session_answers(tenant_id, session_id, slot, player_id, answer, received_at)
                values (@tenantId, @sessionId, @slot, @playerId, jsonb_build_object('index', @slot), now());
                """, new { tenantId = Host.TenantId, sessionId, slot, playerId, prompt = $"Question {slot}" });
        }
        return sessionId;
    }
}
