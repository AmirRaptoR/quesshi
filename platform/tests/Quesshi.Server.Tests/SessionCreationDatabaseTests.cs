using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
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

        Assert.Equal(SessionCreateStatus.IdConflict, result.Status);
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

        Assert.Equal(SessionCreateStatus.IdConflict, result.Status);
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

        var row = await database.InTenantAsync(seeded.TenantA, tx => tx.QuerySingleOrDefaultAsync<(string Status, DateTimeOffset? DueAt, int Seats)>("""
            select status, due_at as DueAt, (select count(*) from session_participants where session_id = sessions.id) as Seats
            from sessions where id = @id
            """, new { id = result.SessionId }));
        Assert.Equal(("lobby", true, 1), (row.Status, row.DueAt is not null, row.Seats));
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
        Assert.True(created.Status == SessionCreateStatus.Created, created.Error);
        var row = await database.InTenantAsync(seeded.TenantA, async tx => (await tx.QueryAsync<(string Status, int Items, int Seats, bool Eligible, DateTimeOffset? DueAt)>("""
            select status, (select count(*)::int from session_items where session_id = sessions.id) as Items,
                   (select count(*)::int from session_participants where session_id = sessions.id) as Seats,
                   (select eligible from session_participants where session_id = sessions.id and player_id = @playerId) as Eligible,
                   due_at as DueAt
            from sessions where id = @id
            """, new { id = created.SessionId, playerId = seeded.CreatorA })).Single());

        Assert.Equal(("running", 10, 1, false, true), (row.Status, row.Items, row.Seats, row.Eligible, row.DueAt is not null));
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
        Assert.True(created.Status == SessionCreateStatus.Created, created.Error);
        var eligible = await database.InTenantAsync(seeded.TenantA, tx => tx.QueryAsync<bool>("select eligible from session_participants where session_id = @id order by seat", new { id = created.SessionId }));

        Assert.Equal([true, true], eligible);
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

    private WebApplication CreateApplication(ISessionCodeGenerator? codes = null, ISource? source = null)
    {
        var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        return Program.CreateApplication(sources, 11920, 31920, configureServices: services =>
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

    private static Picked ChoiceSnapshots(int count) => new PickedSnapshots(Enumerable.Range(0, count).Select(index => new ReadySnapshot(
        "choice", 1, "trivia", 1, null, null, $"Prompt {index}",
        Json("{\"options\":[\"A\",\"B\",\"C\",\"D\"],\"correctIndex\":0}"),
        Json("{}"), Json("{\"options\":[\"A\",\"B\",\"C\",\"D\"]}"),
        Json("{\"correctIndex\":0,\"correctOption\":\"A\"}"))).ToArray());

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
}
