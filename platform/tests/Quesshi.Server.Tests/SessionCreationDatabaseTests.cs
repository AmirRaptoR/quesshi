using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.MapPick;
using Quesshi.Kinds.Order;
using Quesshi.Kinds.PollChoice;
using Quesshi.Kinds.PollRoster;
using Quesshi.Plugin.Api;
using Quesshi.Shared;
using Quesshi.Server;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class SessionCreationDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Creation_replays_only_the_same_tenant_creator_and_request()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());

        var created = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));
        var replayed = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        Assert.Equal((SessionCreateStatus.Replayed, created.SessionId, created.Code), (replayed.Status, replayed.SessionId, replayed.Code));
    }

    [SkippableFact]
    public async Task Creation_replays_the_original_request_after_the_session_has_progressed()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());

        var created = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));
        await using (var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner")))
        {
            await owner.OpenAsync();
            await owner.ExecuteAsync("""
                update sessions set status = 'running', started_at = created_at, phase = 'play', slot = 0
                where tenant_id = @tenantId and id = @sessionId;
                insert into session_participants(tenant_id, session_id, player_id, seat, role)
                values (@tenantId, @sessionId, @playerId, 1, 'player');
                """, new { tenantId = seeded.TenantA, sessionId = request.Id, playerId = seeded.OtherA });
        }

        var replayed = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        Assert.Equal((SessionCreateStatus.Replayed, request.Id, created.Code), (replayed.Status, replayed.SessionId, replayed.Code));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_reused_id_with_a_different_capacity()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());
        _ = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request with { Capacity = 3 }));

        Assert.Equal((SessionCreateStatus.IdConflict, null, null), (result.Status, result.SessionId, result.Code));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_reused_id_with_a_different_shape()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());
        _ = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request with { Shape = SessionShape.Open }));

        Assert.Equal((SessionCreateStatus.IdConflict, null, null), (result.Status, result.SessionId, result.Code));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_reused_id_with_different_content_parameters()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());
        _ = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA,
            request with { Content = request.Content with { Parameters = Json("{\"level\":2}") } }));

        Assert.Equal((SessionCreateStatus.IdConflict, null, null), (result.Status, result.SessionId, result.Code));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_reused_id_with_a_different_content_count()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());
        _ = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA,
            request with { Content = request.Content with { Count = 9 } }));

        Assert.Equal((SessionCreateStatus.IdConflict, null, null), (result.Status, result.SessionId, result.Code));
    }

    [SkippableFact]
    public async Task Creation_replays_an_open_request_with_its_original_id_and_code()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("source", "ready"), ("kind", "choice"));
        await using var app = CreateApplication(source: new FixedSource(ChoiceSnapshots(10)));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid()) with { Content = new("ready", Json("{}"), 10), Shape = SessionShape.Open };

        var created = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));
        var replayed = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        Assert.Equal((SessionCreateStatus.Replayed, created.SessionId, created.Code), (replayed.Status, replayed.SessionId, replayed.Code));
    }

    [SkippableFact]
    public async Task Creation_replays_a_started_roster_with_its_original_id_and_code()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("source", "ready"), ("kind", "choice"));
        await using var app = CreateApplication(source: new FixedSource(ChoiceSnapshots(10)));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid()) with
        {
            Content = new("ready", Json("{}"), 10),
            Shape = SessionShape.Started,
            Participants = [new(seeded.CreatorA), new(seeded.OtherA)],
        };

        var created = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));
        var replayed = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        Assert.Equal((SessionCreateStatus.Replayed, created.SessionId, created.Code), (replayed.Status, replayed.SessionId, replayed.Code));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_reused_id_with_a_different_started_roster()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("source", "ready"), ("kind", "choice"));
        await using var app = CreateApplication(source: new FixedSource(ChoiceSnapshots(10)));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid()) with
        {
            Content = new("ready", Json("{}"), 10),
            Shape = SessionShape.Started,
            Participants = [new(seeded.CreatorA), new(seeded.OtherA)],
        };
        _ = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA,
            request with { Participants = [new(seeded.CreatorA), new(seeded.GuestA)] }));

        Assert.Equal((SessionCreateStatus.IdConflict, null, null), (result.Status, result.SessionId, result.Code));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_reused_id_with_a_different_creator()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());
        _ = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.OtherA, request));

        Assert.Equal((SessionCreateStatus.IdConflict, null, null), (result.Status, result.SessionId, result.Code));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_reused_id_with_a_different_format()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());
        _ = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request with { Format = "other" }));

        Assert.Equal((SessionCreateStatus.IdConflict, null, null), (result.Status, result.SessionId, result.Code));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_reused_id_with_different_content()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());
        _ = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request with { Content = new("other", Json("{}"), 10) }));

        Assert.Equal((SessionCreateStatus.IdConflict, null, null), (result.Status, result.SessionId, result.Code));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_reused_id_with_different_rules()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());
        _ = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request with { Rules = Json("{\"x\":1}") }));

        Assert.Equal((SessionCreateStatus.IdConflict, null, null), (result.Status, result.SessionId, result.Code));
    }

    [SkippableFact]
    public async Task Creation_allows_the_same_id_in_another_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());
        _ = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        var result = await database.InTenantAsync(seeded.TenantB, tx => sessions.CreateAsync(tx, seeded.CreatorB, request));

        Assert.Equal(SessionCreateStatus.Created, result.Status);
    }

    [SkippableFact]
    public async Task Lobby_creation_persists_owner_seat_and_async_due_date()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, Request(Guid.NewGuid())));

        var row = await database.InTenantAsync(seeded.TenantA, tx => tx.QuerySingleOrDefaultAsync<(string Status, DateTimeOffset? DueAt, int Seats)>("""
            select status, due_at as DueAt, (select count(*) from session_participants where session_id = sessions.id) as Seats
            from sessions where id = @id
            """, new { id = result.SessionId }));
        Assert.Equal(("lobby", true, 1), (row.Status, row.DueAt is not null, row.Seats));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_capacity_above_the_tenant_limit()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid()) with { Capacity = 21 };

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        Assert.Equal((SessionCreateStatus.Invalid, "invalid_capacity"), (result.Status, result.Error));
    }

    [SkippableFact]
    public async Task Creation_honors_a_configured_tenant_capacity()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await SetCapacityAsync(seeded.TenantA, 1);
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, Request(Guid.NewGuid()) with { Capacity = 2 }));

        Assert.Equal((SessionCreateStatus.Invalid, "invalid_capacity"), (result.Status, result.Error));
    }

    [SkippableFact]
    public async Task Creation_caps_a_tenant_capacity_setting_at_500()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await SetCapacityAsync(seeded.TenantA, 900);
        await EnableAsync(seeded.TenantA, ("format", "large_lobby"));
        await using var app = CreateApplication(formats: new FormatRegistry([new LargeLobbyFormat()]));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA,
            new SessionCreateRequest(Guid.NewGuid(), "large_lobby", new("bank", Json("{}"), 1), Json("{}"), 501, SessionShape.Lobby)));

        Assert.Equal((SessionCreateStatus.Invalid, "invalid_capacity"), (result.Status, result.Error));
    }

    [SkippableFact]
    public async Task Creation_rejects_guests_when_the_format_disallows_them()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("format", "large_lobby"));
        await using var app = CreateApplication(formats: new FormatRegistry([new LargeLobbyFormat()]));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.GuestA,
            new SessionCreateRequest(Guid.NewGuid(), "large_lobby", new("bank", Json("{}"), 1), Json("{}"), 1, SessionShape.Lobby)));

        Assert.Equal((SessionCreateStatus.Invalid, "guest_not_allowed"), (result.Status, result.Error));
    }

    [SkippableFact]
    public async Task Creation_rejects_an_unentitled_format_even_when_it_is_enabled()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using (var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner")))
        {
            await owner.OpenAsync();
            await owner.ExecuteAsync("update tenant_capabilities set entitled = false, enabled = true where tenant_id = @tenantId and type = 'format' and key = 'async_duel'", new { tenantId = seeded.TenantA });
        }
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, Request(Guid.NewGuid())));

        Assert.Equal(SessionCreateStatus.FormatUnavailable, result.Status);
    }

    [SkippableFact]
    public async Task Creation_rejects_a_started_roster_below_the_format_player_range()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid()) with { Shape = SessionShape.Started, Participants = [new(seeded.CreatorA)] };

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        Assert.Equal((SessionCreateStatus.Invalid, "invalid_roster"), (result.Status, result.Error));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_roster_role_the_format_does_not_allow()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid()) with
        {
            Shape = SessionShape.Started,
            Participants = [new(seeded.CreatorA), new(seeded.OtherA, "spectator")],
        };

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));

        Assert.Equal((SessionCreateStatus.Invalid, "invalid_roster"), (result.Status, result.Error));
    }

    [SkippableFact]
    public async Task Creation_retries_an_active_code_collision()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var first = CreateApplication(new QueueCodes("ABCDEF"));
        var firstSessions = first.Services.GetRequiredService<ISessions>();
        var firstDatabase = first.Services.GetRequiredService<ITenantDb>();
        _ = await firstDatabase.InTenantAsync(seeded.TenantA, tx => firstSessions.CreateAsync(tx, seeded.CreatorA, Request(Guid.NewGuid())));
        await using var second = CreateApplication(new QueueCodes("ABCDEF", "BCDEFG"));
        var secondSessions = second.Services.GetRequiredService<ISessions>();
        var secondDatabase = second.Services.GetRequiredService<ITenantDb>();

        var created = await secondDatabase.InTenantAsync(seeded.TenantA, tx => secondSessions.CreateAsync(tx, seeded.CreatorA, Request(Guid.NewGuid())));

        Assert.Equal("BCDEFG", created.Code);
    }

    [SkippableFact]
    public async Task Creation_keeps_concurrent_active_codes_unique()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication(new CollisionThenUniqueCodes());
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var results = await Task.WhenAll(
            database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, Request(Guid.NewGuid()))),
            database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, Request(Guid.NewGuid()))));

        Assert.NotEqual(results[0].Code, results[1].Code);
    }

    [SkippableFact]
    public async Task Creation_reuses_a_finished_sessions_code()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var first = CreateApplication(new QueueCodes("ABCDEF"));
        var firstSessions = first.Services.GetRequiredService<ISessions>();
        var firstDatabase = first.Services.GetRequiredService<ITenantDb>();
        var firstResult = await firstDatabase.InTenantAsync(seeded.TenantA, tx => firstSessions.CreateAsync(tx, seeded.CreatorA, Request(Guid.NewGuid())));
        await using (var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner")))
        {
            await owner.OpenAsync();
            await owner.ExecuteAsync("update sessions set status = 'finished', ended_at = created_at, end_reason = 'forfeit', state = '{}', due_at = null where tenant_id = @tenantId and id = @id", new { tenantId = seeded.TenantA, id = firstResult.SessionId });
        }
        await using var second = CreateApplication(new QueueCodes("ABCDEF"));
        var secondSessions = second.Services.GetRequiredService<ISessions>();
        var secondDatabase = second.Services.GetRequiredService<ITenantDb>();

        var result = await secondDatabase.InTenantAsync(seeded.TenantA, tx => secondSessions.CreateAsync(tx, seeded.CreatorA, Request(Guid.NewGuid())));

        Assert.Equal("ABCDEF", result.Code);
    }

    [SkippableFact]
    public async Task Creation_generates_unique_six_character_codes_from_the_legacy_alphabet()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var created = await Task.WhenAll(
            database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, Request(Guid.NewGuid()))),
            database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, Request(Guid.NewGuid()))));
        var storedCodes = await database.InTenantAsync(seeded.TenantA, tx => tx.QueryAsync<(Guid Id, string Code)>(
            "select id, code from sessions where id = any(@ids)", new { ids = created.Select(result => result.SessionId!.Value).ToArray() }));

        Assert.All(created, result =>
        {
            Assert.Matches("^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{6}$", result.Code);
            Assert.Equal(result.Code, storedCodes.Single(row => row.Id == result.SessionId).Code);
        });
        Assert.NotEqual(created[0].Code, created[1].Code);
    }

    [SkippableFact]
    public async Task Open_creation_starts_async_play_and_persists_ready_snapshots()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("source", "ready"), ("kind", "choice"));
        await using var app = CreateApplication(source: new FixedSource(ChoiceSnapshots(10)));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = new SessionCreateRequest(Guid.NewGuid(), "async_duel", new("ready", Json("{}"), 10), Json("{}"), 2, SessionShape.Open);

        var created = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));
        var row = await database.InTenantAsync(seeded.TenantA, async tx => (await tx.QueryAsync<(string Status, int Capacity, int Items, int Seats, bool Eligible, DateTimeOffset? DueAt, DateTimeOffset? StartedAt)>("""
            select status, capacity, (select count(*)::int from session_items where session_id = sessions.id) as Items,
                   (select count(*)::int from session_participants where session_id = sessions.id) as Seats,
                   (select eligible from session_participants where session_id = sessions.id and player_id = @playerId) as Eligible,
                   due_at as DueAt, started_at as StartedAt
            from sessions where id = @id
            """, new { id = created.SessionId, playerId = seeded.CreatorA })).Single());

        Assert.Equal(("running", 2, 10, 1, false, true, true), (row.Status, row.Capacity, row.Items, row.Seats, row.Eligible, row.DueAt is not null, row.StartedAt is not null));
    }

    [SkippableFact]
    public async Task Creation_keeps_card_reveal_grading_and_category_presentation_after_source_content_changes()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("source", "ids"), ("kind", "choice"));
        var questionIds = await InsertQuestionsAsync(seeded.TenantA, 10);
        await using var app = CreateApplication(source: new FixedSource(new PickedQuestionIds(questionIds), "ids"));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var loader = app.Services.GetRequiredService<ISessionSnapshotLoader>();
        var kinds = app.Services.GetRequiredService<IKindRegistry>();
        var request = new SessionCreateRequest(Guid.NewGuid(), "async_duel", new("ids", Json("{}"), 10), Json("{}"), 2, SessionShape.Open);

        var created = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));
        var before = (await loader.LoadItemsAsync(seeded.TenantA, created.SessionId!.Value, [0])).Single();
        var beforeCategory = await ItemCategoryAsync(seeded.TenantA, created.SessionId.Value);
        await ChangeThenDeleteQuestionsAsync(seeded.TenantA, questionIds);
        var after = (await loader.LoadItemsAsync(seeded.TenantA, created.SessionId.Value, [0])).Single();
        var afterCategory = await ItemCategoryAsync(seeded.TenantA, created.SessionId.Value);
        var beforeGrade = kinds.Get(before.Metadata.Kind, before.Metadata.KindVersion).Grade(before.Payload, before.Resolved, Json("{\"index\":0}"));
        var afterGrade = kinds.Get(after.Metadata.Kind, after.Metadata.KindVersion).Grade(after.Payload, after.Resolved, Json("{\"index\":0}"));

        Assert.Equal((before.Card.GetRawText(), before.Reveal.GetRawText(), beforeGrade, beforeCategory),
            (after.Card.GetRawText(), after.Reveal.GetRawText(), afterGrade, afterCategory));
    }

    [SkippableFact]
    public async Task Creation_rejects_a_ready_snapshot_with_an_invalid_kind_version()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("source", "ready"), ("kind", "choice"));
        var badVersion = ((PickedSnapshots)ChoiceSnapshots(10)).Items.ToArray();
        badVersion[0] = badVersion[0] with { KindVersion = 99 };
        await using var app = CreateApplication(source: new FixedSource(new PickedSnapshots(badVersion)));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA,
            new SessionCreateRequest(Guid.NewGuid(), "async_duel", new("ready", Json("{}"), 10), Json("{}"), 2, SessionShape.Open)));

        Assert.Equal((SessionCreateStatus.Invalid, "invalid_source_result"), (result.Status, result.Error));
    }

    [SkippableFact]
    public async Task Creation_upgrades_a_drawn_question_to_the_current_kind_version()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("source", "ids"), ("kind", "choice"));
        var questionIds = await InsertVersionOneChoiceQuestionsAsync(seeded.TenantA, 10);
        await using var app = CreateApplication(source: new FixedSource(new PickedQuestionIds(questionIds), "ids"), kinds: VersionedChoiceKinds());
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA,
            new SessionCreateRequest(Guid.NewGuid(), "async_duel", new("ids", Json("{}"), 10), Json("{}"), 2, SessionShape.Open)));
        var item = await ItemUpgradeValuesAsync(database, seeded.TenantA, created.SessionId!.Value);

        Assert.Equal((2, "upgraded", "{}", "upgraded", "upgraded"), item);
    }

    [SkippableFact]
    public async Task Creation_upgrades_a_ready_snapshot_to_the_current_kind_version()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("source", "ready"), ("kind", "choice"));
        await using var app = CreateApplication(source: new FixedSource(VersionOneChoiceSnapshots(10)), kinds: VersionedChoiceKinds());
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA,
            new SessionCreateRequest(Guid.NewGuid(), "async_duel", new("ready", Json("{}"), 10), Json("{}"), 2, SessionShape.Open)));
        var item = await ItemUpgradeValuesAsync(database, seeded.TenantA, created.SessionId!.Value);

        Assert.Equal((2, "upgraded", "upgraded", "upgraded", "upgraded"), item);
    }

    [SkippableFact]
    public async Task Started_shared_pacing_marks_account_players_eligible_at_start()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("format", "poll"), ("source", "ready"), ("kind", "poll_choice"));
        await using var app = CreateApplication(source: new FixedSource(PollSnapshots(1)));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = new SessionCreateRequest(Guid.NewGuid(), "poll", new("ready", Json("{}"), 1), Json("{}"), 2, SessionShape.Started,
            [new(seeded.CreatorA), new(seeded.OtherA)]);

        var created = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));
        var row = await database.InTenantAsync(seeded.TenantA, async tx => (await tx.QueryAsync<(string Status, int Items, int Seats, DateTimeOffset? StartedAt, string Eligible)>("""
            select status, (select count(*)::int from session_items where session_id = sessions.id) as Items,
                   (select count(*)::int from session_participants where session_id = sessions.id) as Seats, started_at as StartedAt
                   , (select array_agg(eligible order by seat)::text from session_participants where session_id = sessions.id) as Eligible
            from sessions where id = @id
            """, new { id = created.SessionId })).Single());

        Assert.Equal(("running", 1, 2, true, "{t,t}"), (row.Status, row.Items, row.Seats, row.StartedAt is not null, row.Eligible));
    }

    [SkippableFact]
    public async Task Started_shared_pacing_keeps_a_guest_ineligible()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("format", "poll"), ("source", "ready"), ("kind", "poll_choice"));
        await using var app = CreateApplication(source: new FixedSource(PollSnapshots(1)));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = new SessionCreateRequest(Guid.NewGuid(), "poll", new("ready", Json("{}"), 1), Json("{}"), 2, SessionShape.Started,
            [new(seeded.CreatorA), new(seeded.GuestA)]);

        var created = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));
        Assert.True(created.Status == SessionCreateStatus.Created, created.Error);
        var eligible = await database.InTenantAsync(seeded.TenantA, tx => tx.QueryAsync<bool>("select eligible from session_participants where session_id = @id order by seat", new { id = created.SessionId }));

        Assert.Equal([true, false], eligible);
    }

    [SkippableFact]
    public async Task Started_personal_pacing_keeps_account_players_ineligible_until_the_first_card()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("source", "ready"), ("kind", "choice"));
        await using var app = CreateApplication(source: new FixedSource(ChoiceSnapshots(10)));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = new SessionCreateRequest(Guid.NewGuid(), "async_duel", new("ready", Json("{}"), 10), Json("{}"), 2, SessionShape.Started,
            [new(seeded.CreatorA), new(seeded.OtherA)]);

        var created = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));
        var eligible = await database.InTenantAsync(seeded.TenantA, tx => tx.QueryAsync<bool>("select eligible from session_participants where session_id = @id order by seat", new { id = created.SessionId }));

        Assert.Equal([false, false], eligible);
    }

    [SkippableFact]
    public async Task Creation_returns_source_unavailable_without_a_partial_session()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = new SessionCreateRequest(Guid.NewGuid(), "async_duel", new("missing", Json("{}"), 10), Json("{}"), 2, SessionShape.Open);

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));
        var count = await database.InTenantAsync(seeded.TenantA, async tx => (await tx.QueryAsync<int>("select count(*)::int from sessions where id = @id", new { id = request.Id })).Single());

        Assert.Equal((SessionCreateStatus.SourceUnavailable, 0), (result.Status, count));
    }

    [SkippableFact]
    public async Task Creation_returns_not_enough_questions_without_a_partial_session()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await EnableAsync(seeded.TenantA, ("source", "empty"));
        await using var app = CreateApplication(source: new FixedSource(new NotEnoughQuestionsException(0, 10), "empty"));
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = new SessionCreateRequest(Guid.NewGuid(), "async_duel", new("empty", Json("{}"), 10), Json("{}"), 2, SessionShape.Open);

        var result = await database.InTenantAsync(seeded.TenantA, tx => sessions.CreateAsync(tx, seeded.CreatorA, request));
        var count = await database.InTenantAsync(seeded.TenantA, async tx => (await tx.QueryAsync<int>("select count(*)::int from sessions where id = @id", new { id = request.Id })).Single());

        Assert.Equal((SessionCreateStatus.NotEnoughQuestions, 0), (result.Status, count));
    }

    [SkippableFact]
    public async Task Caller_rollback_removes_a_created_lobby_and_its_owner_seat()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var app = CreateApplication();
        var sessions = app.Services.GetRequiredService<ISessions>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var request = Request(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InTenantAsync(seeded.TenantA, async tx =>
        {
            _ = await sessions.CreateAsync(tx, seeded.CreatorA, request);
            return await Task.FromException<int>(new InvalidOperationException("rollback"));
        }));
        var count = await database.InTenantAsync(seeded.TenantA, async tx => (await tx.QueryAsync<int>("select count(*)::int from sessions where id = @id", new { id = request.Id })).Single());

        Assert.Equal(0, count);
    }

    private async Task<(Guid TenantA, Guid TenantB, Guid CreatorA, Guid OtherA, Guid GuestA, Guid CreatorB)> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var creatorA = Guid.NewGuid(); var otherA = Guid.NewGuid(); var guestA = Guid.NewGuid(); var creatorB = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await connection.OpenAsync();
        await connection.ExecuteAsync("delete from session_answers; delete from session_items; delete from session_participants; delete from sessions; delete from players; delete from tenant_capabilities; delete from tenant_languages; delete from tenants");
        await connection.ExecuteAsync("insert into tenants(id,slug,name,default_language) values (@a,'sessions-a','A','en'),(@b,'sessions-b','B','en'); insert into tenant_languages(tenant_id,code,name,english_name) values (@a,'en','English','English'),(@b,'en','English','English');", new { a, b });
        await connection.ExecuteAsync("insert into accounts(id) values (@creatorA),(@otherA),(@creatorB)", new { creatorA, otherA, creatorB });
        await connection.ExecuteAsync("insert into players(tenant_id,id,account_id,display_name,avatar_seed,lang) values (@a,@creatorA,@creatorA,'Creator','a','en'),(@a,@otherA,@otherA,'Other','b','en'),(@a,@guestA,null,'Guest','g','en'),(@b,@creatorB,@creatorB,'Creator B','c','en')", new { a, b, creatorA, otherA, guestA, creatorB });
        await connection.ExecuteAsync("insert into tenant_capabilities(tenant_id,type,key,entitled,enabled) values (@a,'format','async_duel',true,true),(@b,'format','async_duel',true,true)", new { a, b });
        return (a, b, creatorA, otherA, guestA, creatorB);
    }

    private async Task EnableAsync(Guid tenantId, params (string Type, string Key)[] capabilities)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        foreach (var capability in capabilities)
            await owner.ExecuteAsync("insert into tenant_capabilities(tenant_id,type,key,entitled,enabled) values (@tenantId,@type,@key,true,true) on conflict (tenant_id,type,key) do update set entitled = true, enabled = true", new { tenantId, type = capability.Type, key = capability.Key });
    }

    private async Task SetCapacityAsync(Guid tenantId, int capacity)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("""
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled, settings)
            values (@tenantId, 'switch', 'sessions', true, true, jsonb_build_object('capacity', @capacity))
            on conflict (tenant_id, type, key) do update set settings = excluded.settings
            """, new { tenantId, capacity });
    }

    private async Task<IReadOnlyList<Guid>> InsertQuestionsAsync(Guid tenantId, int count)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        var categoryId = Guid.NewGuid();
        await owner.ExecuteAsync("insert into categories(tenant_id, id, slug, names) values (@tenantId, @id, 'snapshot-category', '{\"en\":\"Original category\"}'::jsonb)", new { tenantId, id = categoryId });
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
            await owner.ExecuteAsync("""
                insert into questions(tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source)
                values (@tenantId, @id, 'choice', 1, 'trivia', 'en', @categoryId, 1, @prompt,
                        '{"options":["original","b","c","d"],"correctIndex":0}'::jsonb, @prompt, 'approved', 'admin')
                """, new { tenantId, id, categoryId, prompt = $"Original prompt {index}" });
        return ids;
    }

    private async Task<IReadOnlyList<Guid>> InsertVersionOneChoiceQuestionsAsync(Guid tenantId, int count)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
            await owner.ExecuteAsync("""
                insert into questions(tenant_id, id, kind, kind_version, family, lang, level, prompt, payload, fingerprint, status, source)
                values (@tenantId, @id, 'choice', 1, 'trivia', 'en', 1, @prompt,
                        '{"choices":["upgraded","b","c","d"],"correctIndex":0}'::jsonb, @prompt, 'approved', 'admin')
                """, new { tenantId, id, prompt = $"Version one prompt {index}" });
        return ids;
    }

    private async Task ChangeThenDeleteQuestionsAsync(Guid tenantId, IReadOnlyList<Guid> questionIds)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("update questions set prompt = 'changed', payload = '{\"options\":[\"changed\",\"b\",\"c\",\"d\"],\"correctIndex\":1}'::jsonb where tenant_id = @tenantId and id = any(@questionIds)", new { tenantId, questionIds });
        await owner.ExecuteAsync("delete from questions where tenant_id = @tenantId and id = any(@questionIds); delete from categories where tenant_id = @tenantId and slug = 'snapshot-category'", new { tenantId, questionIds });
    }

    private async Task<string?> ItemCategoryAsync(Guid tenantId, Guid sessionId)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        return await owner.QuerySingleAsync<string?>("select category::text from session_items where tenant_id = @tenantId and session_id = @sessionId and slot = 0", new { tenantId, sessionId });
    }

    private WebApplication CreateApplication(ISessionCodeGenerator? codes = null, ISource? source = null, IFormatRegistry? formats = null, IKindRegistry? kinds = null)
    {
        var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        return Program.CreateApplication(sources, 11920, 31920, configuredFormats: formats, configuredKinds: kinds, configureServices: services =>
        {
            services.AddSingleton<IClock>(new TestClock(DateTimeOffset.Parse("2026-10-20T00:00:00Z")));
            if (codes is not null) services.AddSingleton(codes);
            if (source is not null) services.AddSingleton<ISourceRegistry>(new SingleSourceRegistry(source));
        });
    }

    private static SessionCreateRequest Request(Guid id) => new(id, "async_duel", new SessionContent("bank", Json("{}"), 10), Json("{}"), 2, SessionShape.Lobby);
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }

    private sealed class QueueCodes(params string[] values) : ISessionCodeGenerator
    {
        private readonly ConcurrentQueue<string> _values = new(values);

        public string Next() => _values.TryDequeue(out var value) ? value : throw new InvalidOperationException("The test did not provide another code.");
    }

    private sealed class CollisionThenUniqueCodes : ISessionCodeGenerator
    {
        private int _calls;

        public string Next() => Interlocked.Increment(ref _calls) switch
        {
            <= 2 => "ABCDEF",
            3 => "BCDEFG",
            _ => "CDEFGH",
        };
    }

    private sealed class VersionTwoChoiceKind : ChoiceKind
    {
        public override int Version => 2;

        public override ChoicePayload Upgrade(int from, JsonElement payload)
        {
            if (from == 1)
            {
                if (!payload.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                    || !payload.TryGetProperty("correctIndex", out var correctIndex) || !correctIndex.TryGetInt32(out var index))
                    throw new JsonException("Choice v1 payload is invalid.");
                return new ChoicePayload(choices.EnumerateArray().Select(option => option.GetString() ?? throw new JsonException("Choice options are invalid.")).ToArray(), index);
            }
            if (from != Version) throw new NotSupportedException($"choice v{from} cannot be upgraded by choice v{Version}.");
            return JsonSerializer.Deserialize(payload, ChoiceJsonContext.Default.ChoicePayload)
                ?? throw new JsonException("Choice payload is invalid.");
        }

        public override Snapshot<ChoiceCard, ChoiceReveal, ChoiceStored> UpgradeSnapshot(int from, RawSnapshot old)
        {
            if (from != 1) return base.UpgradeSnapshot(from, old);
            return new(new ChoiceCard(["upgraded", "b", "c", "d"]), new ChoiceReveal(0, "upgraded"), []);
        }
    }

    private static Picked ChoiceSnapshots(int count) => new PickedSnapshots(Enumerable.Range(0, count).Select(index => new ReadySnapshot(
        "choice", 1, "trivia", 1, null, null, $"Prompt {index}",
        Json("{\"options\":[\"A\",\"B\",\"C\",\"D\"],\"correctIndex\":0}"),
        Json("{}"), Json("{\"options\":[\"A\",\"B\",\"C\",\"D\"]}"),
        Json("{\"correctIndex\":0,\"correctOption\":\"A\"}"))).ToArray());

    private static Picked VersionOneChoiceSnapshots(int count) => new PickedSnapshots(Enumerable.Range(0, count).Select(index => new ReadySnapshot(
        "choice", 1, "trivia", 1, null, null, $"Version one prompt {index}",
        Json("{\"choices\":[\"upgraded\",\"b\",\"c\",\"d\"],\"correctIndex\":0}"),
        Json("{\"source\":\"upgraded\"}"), Json("{\"options\":[\"old\",\"b\",\"c\",\"d\"]}"),
        Json("{\"correctIndex\":0,\"correctOption\":\"old\"}"))).ToArray());

    private static IKindRegistry VersionedChoiceKinds() => new KindRegistry([
        new ChoiceKind(),
        new VersionTwoChoiceKind(),
        new OrderKind(),
        new MapPickKind(),
        new PollChoiceKind(),
        new PollRosterKind()
    ]);

    private static async Task<(int KindVersion, string PayloadOption, string ResolvedSource, string CardOption, string RevealOption)> ItemUpgradeValuesAsync(ITenantDb database, Guid tenantId, Guid sessionId) =>
        await database.InTenantAsync(tenantId, async tx => (await tx.QueryAsync<(int KindVersion, string PayloadOption, string ResolvedSource, string CardOption, string RevealOption)>("""
            select kind_version as KindVersion, payload -> 'options' ->> 0 as PayloadOption,
                   coalesce(resolved ->> 'source', '{}') as ResolvedSource, card -> 'options' ->> 0 as CardOption,
                   reveal ->> 'correctOption' as RevealOption
            from session_items where session_id = @sessionId and slot = 0
            """, new { sessionId })).Single());

    private static Picked PollSnapshots(int count) => new PickedSnapshots(Enumerable.Range(0, count).Select(index => new ReadySnapshot(
        "poll_choice", 1, "poll", null, null, null, $"Poll {index}",
        Json("{\"options\":[{\"id\":\"yes\",\"text\":\"Yes\"},{\"id\":\"no\",\"text\":\"No\"}]}"),
        Json("{}"), Json("{\"options\":[{\"id\":\"yes\",\"text\":\"Yes\"},{\"id\":\"no\",\"text\":\"No\"}]}"), Json("{}"))).ToArray());

    private sealed class FixedSource : ISource
    {
        private readonly Picked? _picked;
        private readonly Exception? _exception;

        public FixedSource(Picked picked, string id = "ready") => (_picked, Id) = (picked, id);
        public FixedSource(Exception exception, string id) => (_exception, Id) = (exception, id);
        public string Id { get; }
        public Type Params => typeof(object);
        public FeatureSettings Settings { get; } = FeatureSettings.Create(new object(), _ => []);
        public Task<bool> AuthorizeAsync(ITenantTx transaction, JsonElement parameters, Guid actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<Picked> PickAsync(ITenantTx transaction, JsonElement parameters, SourceDrawPlan plan, Guid actor, CancellationToken cancellationToken = default) =>
            _exception is null ? Task.FromResult(_picked!) : Task.FromException<Picked>(_exception);
    }

    private sealed class SingleSourceRegistry(ISource source) : ISourceRegistry
    {
        public ISource Get(string id) => TryGet(id, out var found) ? found! : throw new KeyNotFoundException();
        public bool TryGet(string id, out ISource? found)
        {
            found = string.Equals(id, source.Id, StringComparison.Ordinal) ? source : null;
            return found is not null;
        }
    }

    private sealed class LargeLobbyFormat : IFormat
    {
        public string Id => "large_lobby";
        public FormatSpec Spec { get; } = new(Pacing.Personal, 1, 600, true, false, _ => true, false, null, false, [new Role("player")], TimeSpan.Zero, new Dictionary<string, Type>());
        public FormatVersions Versions { get; } = new(1, 1, 1);
        public JsonElement ReadRules(JsonElement raw) => raw;
        public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(content.Count, []);
        public void Start(ISession session) { }
        public void Apply(ISession session, Command command) { }
        public void Timeout(ISession session) { }
        public object View(IViewing viewing) => new { };
        public JsonElement UpgradeState(int from, JsonElement state) => state;
        public JsonElement UpgradeRules(int from, JsonElement rules) => rules;
        public JsonElement UpgradeResult(int from, JsonElement result) => result;
    }
}
