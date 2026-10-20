using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class SessionEndpointDatabaseTests(PostgresFixture postgres) : IAsyncLifetime
{
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null) return;
        _host = new PlatformHttpFixture([new SessionHttpFeature()]);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Session_creation_rejects_an_invalid_request()
    {
        var host = Host;
        var invalid = await host.Player.PostAsJsonAsync("/api/sessions", new { });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [SkippableFact]
    public async Task Session_creation_rejects_a_nonempty_request_with_an_invalid_capacity()
    {
        var host = Host;
        await host.SetCapabilityAsync("format", "async_duel", true);
        var invalid = await host.Player.PostAsJsonAsync("/api/sessions", new
        {
            id = Guid.NewGuid(),
            format = "async_duel",
            content = new { source = "bank", parameters = new { }, count = 10 },
            rules = new { },
            capacity = 0,
        });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [SkippableFact]
    public async Task Session_creation_rejects_a_guest()
    {
        var guest = await Host.Guest.PostAsJsonAsync("/api/sessions", Lobby(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, guest.StatusCode);
    }

    [SkippableFact]
    public async Task Session_creation_rejects_a_caller_supplied_started_roster()
    {
        var host = Host;
        await host.SetCapabilityAsync("format", "async_duel", true);
        var id = Guid.NewGuid();
        var rejected = await host.Player.PostAsJsonAsync("/api/sessions", new
        {
            id,
            format = "async_duel",
            content = new { source = "bank", parameters = new { }, count = 10 },
            rules = new { },
            capacity = 2,
            shape = "started",
            participants = new[] { new { playerId = Guid.NewGuid(), role = "player" } },
        });
        var absent = await host.Player.GetAsync($"/api/sessions/{id}");

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound), (rejected.StatusCode, absent.StatusCode));
    }

    [SkippableFact]
    public async Task Disabled_format_rejects_new_session_creation()
    {
        var disabled = await Host.Player.PostAsJsonAsync("/api/sessions", Lobby(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
    }

    [SkippableFact]
    public async Task Session_view_returns_a_database_derived_view_to_a_seated_player()
    {
        var host = Host;
        await host.SetCapabilityAsync("format", "async_duel", true);
        var id = Guid.NewGuid();
        var created = await host.Player.PostAsJsonAsync("/api/sessions", Lobby(id));
        var otherAccount = Guid.NewGuid();
        var otherPlayer = Guid.NewGuid();
        await host.ExecuteOwnerAsync("""
            insert into accounts(id, email) values (@otherAccount, @email);
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @otherPlayer, @otherAccount, 'Other player', 'other', 'en');
            insert into session_participants(tenant_id, session_id, player_id, seat, role, phase, slot, slot_started_at)
            values (@tenantId, @id, @otherPlayer, 1, 'player', '1', 0, now());
            insert into session_items(tenant_id, session_id, slot, kind, kind_version, prompt, payload, resolved, card, reveal)
            values (@tenantId, @id, 0, 'choice', 1, 'Persisted prompt', '{"options":[]}'::jsonb, '{}'::jsonb,
                    '{"text":"persisted-card"}'::jsonb, '{"text":"reveal-secret"}'::jsonb);
            insert into session_answers(tenant_id, session_id, slot, player_id, answer, received_at)
            values (@tenantId, @id, 0, @otherPlayer, '{"text":"other-secret"}'::jsonb, now());
            update sessions set status = 'running', phase = '1', slot = 0, started_at = now(), due_at = now() + interval '1 minute'
            where tenant_id = @tenantId and id = @id;
            update session_participants set phase = '1', slot = 0, slot_started_at = now()
            where tenant_id = @tenantId and session_id = @id and player_id = @playerId;
            """, new
        {
            tenantId = host.TenantId,
            id,
            playerId = host.PlayerId,
            otherAccount,
            otherPlayer,
            email = $"other-{otherAccount:N}@example.test",
        });
        var seated = await host.Player.GetAsync($"/api/sessions/{id}");
        var body = await seated.Content.ReadAsStringAsync();
        Assert.True(seated.StatusCode == HttpStatusCode.OK, body);
        using var view = JsonDocument.Parse(body);

        Assert.Equal((HttpStatusCode.Created, id.ToString(), "persisted-card", JsonValueKind.Null, 0),
            (created.StatusCode, view.RootElement.GetProperty("Id").GetString(),
                view.RootElement.GetProperty("Body").GetProperty("You").GetProperty("Card").GetProperty("text").GetString(),
                view.RootElement.GetProperty("Body").GetProperty("You").GetProperty("Reveal").ValueKind,
                view.RootElement.GetProperty("Body").GetProperty("Others").GetArrayLength()));
    }

    [SkippableFact]
    public async Task Session_view_hides_unseated_missing_and_cross_tenant_sessions_with_the_same_error()
    {
        var host = Host;
        await host.SetCapabilityAsync("format", "async_duel", true);
        var id = Guid.NewGuid();
        _ = await host.Player.PostAsJsonAsync("/api/sessions", Lobby(id));
        var unseated = await host.Guest.GetAsync($"/api/sessions/{id}");
        var missing = await host.Guest.GetAsync($"/api/sessions/{Guid.NewGuid()}");
        var otherTenant = await host.CreateOtherTenantPlayerClientAsync();
        var crossTenant = await otherTenant.GetAsync($"/api/sessions/{id}");
        var unseatedError = await unseated.Content.ReadFromJsonAsync<ErrorResponse>();
        var missingError = await missing.Content.ReadFromJsonAsync<ErrorResponse>();
        var crossTenantError = await crossTenant.Content.ReadFromJsonAsync<ErrorResponse>();

        Assert.Equal("not_found", missingError?.Error);
        Assert.Equal((HttpStatusCode.NotFound, missingError), (unseated.StatusCode, unseatedError));
        Assert.Equal((HttpStatusCode.NotFound, missingError), (crossTenant.StatusCode, crossTenantError));
    }

    [SkippableFact]
    public async Task Existing_session_remains_readable_after_its_format_is_disabled()
    {
        var host = Host;
        await host.SetCapabilityAsync("format", "async_duel", true);
        var id = Guid.NewGuid();
        _ = await host.Player.PostAsJsonAsync("/api/sessions", Lobby(id));
        await host.SetCapabilityAsync("format", "async_duel", false);
        var disabled = await host.Player.PostAsJsonAsync("/api/sessions", Lobby(Guid.NewGuid()));
        var retained = await host.Player.GetAsync($"/api/sessions/{id}");

        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(postgres.ConnectionString is null, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private static object Lobby(Guid id) => new
    {
        id,
        format = "async_duel",
        content = new { source = "bank", parameters = new { }, count = 10 },
        rules = new { },
        capacity = 2,
    };

    private sealed record ErrorResponse(string Error);

    private sealed class SessionHttpFeature : IFeature
    {
        public string Id => "session_http";
        public FeatureSettings Settings => FeatureSettings.Create(new SessionHttpSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record SessionHttpSettings;
}
