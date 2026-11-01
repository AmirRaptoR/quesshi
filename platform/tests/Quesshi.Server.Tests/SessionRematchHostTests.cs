using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Features.Features.Friends;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

/// <summary>A rematch started by one player of a finished live session, against the real server host.</summary>
public sealed class SessionRematchHostTests : IAsyncLifetime
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
        await _host.SetCapabilityAsync("format", "live", true);
        await _host.SetCapabilityAsync("kind", "choice", true);
        var category = Guid.NewGuid();
        await _host.ExecuteOwnerAsync("""
            insert into categories(tenant_id, id, slug, names) values (@t, @category, 'rematch-category', '{"en":"Rematch"}'::jsonb);
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled, settings)
            values (@t, 'source', 'bank', true, true, jsonb_build_object('CategoriesByFamily', jsonb_build_object('trivia', jsonb_build_array(@category::text))))
            on conflict (tenant_id, type, key) do update set entitled = true, enabled = true, settings = excluded.settings
            """, new { t = _host.TenantId, category });
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Rematch_started_by_one_player_brings_the_other_player_to_the_same_new_lobby()
    {
        var (second, secondId) = await Host.CreateSecondPlayerClientAsync();
        var finished = await FinishedLiveSessionAsync(secondId);

        using var started = await Host.Player.PostAsync($"/api/sessions/{finished}/rematch", null);
        var lobby = await LobbyIdAsync(started);

        Assert.NotEqual(finished, lobby);
        using var inbox = JsonDocument.Parse(await second.GetStringAsync("/api/me/inbox"));
        var invited = inbox.RootElement.GetProperty("invitations").EnumerateArray().Select(i => i.ToString()).ToArray();
        Assert.Contains(invited, i => i.Contains(lobby.ToString(), StringComparison.OrdinalIgnoreCase));
        using var accepted = await second.PostAsync($"/api/sessions/{lobby}/invites/accept", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var again = await second.PostAsync($"/api/sessions/{finished}/rematch", null);
        Assert.Equal(lobby, await LobbyIdAsync(again));
        Assert.Equal(2, (await Host.QueryOwnerAsync<int>("select count(*)::int from session_participants where session_id = @lobby", new { lobby })).Single());
        Assert.Equal(1, (await Host.QueryOwnerAsync<int>("select count(*)::int from sessions where rematch_of = @finished", new { finished })).Single());
    }

    [SkippableFact]
    public async Task A_guest_cannot_start_a_rematch()
    {
        var (guest, guestId) = await Host.CreateAnotherGuestClientAsync();
        var finished = await FinishedLiveSessionAsync(guestId);

        using var response = await guest.PostAsync($"/api/sessions/{finished}/rematch", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, (await Host.QueryOwnerAsync<int>("select count(*)::int from sessions where rematch_of = @finished", new { finished })).Single());
    }

    private PlatformHttpFixture Host => _host ?? throw new InvalidOperationException("The platform host is not configured.");

    private static async Task<Guid> LobbyIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>A real live lobby, then ended by hand, with the fixture player and <paramref name="otherId"/> seated.</summary>
    private async Task<Guid> FinishedLiveSessionAsync(Guid otherId)
    {
        var id = Guid.NewGuid();
        var created = await Host.Player.PostAsJsonAsync("/api/sessions", new
        {
            id,
            format = "live",
            content = new { source = "bank", parameters = new { }, count = 10 },
            rules = new { },
            capacity = 2,
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        await Host.ExecuteOwnerAsync("""
            insert into session_participants(tenant_id, session_id, player_id, seat, role, status, phase, slot, step)
            values (@t, @id, @other, 1, 'player', 'active', 'idle', -1, 0);
            update sessions set status = 'finished', started_at = now(), ended_at = now(), ended_tz = 'UTC', end_reason = 'completed' where id = @id
            """, new { t = Host.TenantId, id, other = otherId });
        return id;
    }
}
