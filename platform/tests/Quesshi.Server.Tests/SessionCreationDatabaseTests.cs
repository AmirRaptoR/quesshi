using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Application.Sessions;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;
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

        Assert.Equal(created.Code, replayed.Code);
        Assert.Equal(SessionCreateStatus.Replayed, replayed.Status);
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

        Assert.Equal(SessionCreateStatus.IdConflict, result.Status);
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

        var row = await database.InTenantAsync(seeded.TenantA, tx => tx.QuerySingleAsync<(string Status, DateTimeOffset? DueAt, int Seats)>("""
            select status, due_at as DueAt, (select count(*) from session_participants where session_id = sessions.id) as Seats
            from sessions where id = @id
            """, new { id = result.SessionId }));
        Assert.Equal(("lobby", true, 1), (row.Status, row.DueAt is not null, row.Seats));
    }

    private async Task<(Guid TenantA, Guid TenantB, Guid CreatorA, Guid OtherA, Guid CreatorB)> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var creatorA = Guid.NewGuid(); var otherA = Guid.NewGuid(); var creatorB = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await connection.OpenAsync();
        await connection.ExecuteAsync("delete from session_answers; delete from session_items; delete from session_participants; delete from sessions; delete from players; delete from tenant_capabilities; delete from tenant_languages; delete from tenants");
        await connection.ExecuteAsync("insert into tenants(id,slug,name,default_language) values (@a,'sessions-a','A','en'),(@b,'sessions-b','B','en'); insert into tenant_languages(tenant_id,code,name,english_name) values (@a,'en','English','English'),(@b,'en','English','English');", new { a, b });
        await connection.ExecuteAsync("insert into players(tenant_id,id,account_id,display_name,avatar_seed,lang) values (@a,@creatorA,@creatorA,'Creator','a','en'),(@a,@otherA,@otherA,'Other','b','en'),(@b,@creatorB,@creatorB,'Creator B','c','en')", new { a, b, creatorA, otherA, creatorB });
        await connection.ExecuteAsync("insert into tenant_capabilities(tenant_id,type,key,entitled,enabled) values (@a,'format','async_duel',true,true),(@b,'format','async_duel',true,true)", new { a, b });
        return (a, b, creatorA, otherA, creatorB);
    }

    private WebApplication CreateApplication()
    {
        var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        return Program.CreateApplication(sources, 11920, 31920);
    }

    private static SessionCreateRequest Request(Guid id) => new(id, "async_duel", new SessionContent("bank", Json("{}"), 10), Json("{}"), 2, SessionShape.Lobby);
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
}
