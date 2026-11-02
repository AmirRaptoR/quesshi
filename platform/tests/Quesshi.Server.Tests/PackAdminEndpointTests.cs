using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Infrastructure.Security;
using Quesshi.Server.Security;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class PackAdminEndpointTests(PostgresFixture fixture)
{
    private static readonly Guid Tenant = Guid.Parse("3f0c6a52-6d0e-4a55-9a58-0f0a5c1d7e11");
    private const string Host = "packs-admin.example.test";

    [SkippableFact]
    public async Task Editor_creates_renames_describes_and_deletes_a_tenant_owned_pack()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var harness = await StartAsync("editor");

        using var created = await harness.SendAsync(HttpMethod.Post, "/api/admin/packs", JsonContent.Create(new { title = "Capitals", description = "Old", language = "en" }));
        var pack = await created.Content.ReadFromJsonAsync<PackResponse>();
        using var updated = await harness.SendAsync(HttpMethod.Put, $"/api/admin/packs/{pack!.Id}", JsonContent.Create(new { title = "World capitals", description = "New description" }));
        var renamed = await updated.Content.ReadFromJsonAsync<PackResponse>();
        using var listed = await harness.SendAsync(HttpMethod.Get, "/api/admin/packs");
        var list = await listed.Content.ReadFromJsonAsync<PackResponse[]>();
        var ownerOfRow = await harness.Owner.ScalarAsync<object>("select coalesce(owner_id::text, 'tenant') from packs where id = @id", ("id", pack.Id));
        using var deleted = await harness.SendAsync(HttpMethod.Delete, $"/api/admin/packs/{pack.Id}");
        using var gone = await harness.SendAsync(HttpMethod.Get, $"/api/admin/packs/{pack.Id}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("tenant", ownerOfRow);
        Assert.Equal("private", pack.Visibility);
        Assert.Equal(("World capitals", "New description"), (renamed!.Title, renamed.Description));
        Assert.Equal([pack.Id], list!.Select(p => p.Id));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [SkippableFact]
    public async Task Editor_adds_removes_and_reorders_questions_and_sets_visibility()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var harness = await StartAsync("editor");
        var first = await harness.CreateQuestionAsync("First?", "pack");
        var second = await harness.CreateQuestionAsync("Second?", "pack");
        var third = await harness.CreateQuestionAsync("Third?", "admin");
        using var created = await harness.SendAsync(HttpMethod.Post, "/api/admin/packs", JsonContent.Create(new { title = "Ordered", language = "en" }));
        var pack = (await created.Content.ReadFromJsonAsync<PackResponse>())!;
        foreach (var question in new[] { first, second, third })
            Assert.Equal(HttpStatusCode.NoContent, (await harness.SendAsync(HttpMethod.Post, $"/api/admin/packs/{pack.Id}/questions", JsonContent.Create(new { questionId = question }))).StatusCode);

        using var reordered = await harness.SendAsync(HttpMethod.Put, $"/api/admin/packs/{pack.Id}/questions/order", JsonContent.Create(new { ids = new[] { third, first, second } }));
        var afterReorder = await harness.GetDetailAsync(pack.Id);
        using var badOrder = await harness.SendAsync(HttpMethod.Put, $"/api/admin/packs/{pack.Id}/questions/order", JsonContent.Create(new { ids = new[] { third, first } }));
        using var removed = await harness.SendAsync(HttpMethod.Delete, $"/api/admin/packs/{pack.Id}/questions/{first}");
        var afterRemove = await harness.GetDetailAsync(pack.Id);
        using var visible = await harness.SendAsync(HttpMethod.Put, $"/api/admin/packs/{pack.Id}/visibility", JsonContent.Create(new { visibility = "tenant" }));
        using var invalid = await harness.SendAsync(HttpMethod.Put, $"/api/admin/packs/{pack.Id}/visibility", JsonContent.Create(new { visibility = "public" }));
        var final = await harness.GetDetailAsync(pack.Id);

        Assert.Equal(HttpStatusCode.NoContent, reordered.StatusCode);
        Assert.Equal([third, first, second], afterReorder.Questions.Select(q => q.Id));
        Assert.Equal(HttpStatusCode.BadRequest, badOrder.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal([third, second], afterRemove.Questions.Select(q => q.Id));
        Assert.Equal([0, 1], afterRemove.Questions.Select(q => q.Position));
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("tenant", final.Pack.Visibility);
        Assert.Equal(0L, await harness.Owner.ScalarAsync<long>("select count(*) from questions where id = @id", ("id", first)));
        Assert.Equal(1L, await harness.Owner.ScalarAsync<long>("select count(*) from questions where id = @id", ("id", third)));
    }

    [SkippableFact]
    public async Task Caller_without_the_editor_role_is_refused_by_every_pack_route()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var harness = await StartAsync("moderator");
        var pack = await harness.Owner.InsertPackAsync(Tenant, null);
        var question = Guid.NewGuid();

        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, "/api/admin/packs", null),
            (HttpMethod.Get, $"/api/admin/packs/{pack}", null),
            (HttpMethod.Post, "/api/admin/packs", new { title = "Nope", language = "en" }),
            (HttpMethod.Put, $"/api/admin/packs/{pack}", new { title = "Nope" }),
            (HttpMethod.Put, $"/api/admin/packs/{pack}/visibility", new { visibility = "tenant" }),
            (HttpMethod.Delete, $"/api/admin/packs/{pack}", null),
            (HttpMethod.Post, $"/api/admin/packs/{pack}/questions", new { questionId = question }),
            (HttpMethod.Put, $"/api/admin/packs/{pack}/questions/order", new { ids = Array.Empty<Guid>() }),
            (HttpMethod.Delete, $"/api/admin/packs/{pack}/questions/{question}", null),
        };
        foreach (var (method, path, body) in attempts)
        {
            using var response = await harness.SendAsync(method, path, body is null ? null : JsonContent.Create(body));
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} returned {response.StatusCode}");
        }
        using var anonymous = await harness.SendAsync(HttpMethod.Get, "/api/admin/packs", authenticated: false);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(1L, await harness.Owner.ScalarAsync<long>("select count(*) from packs"));
        Assert.Equal("Seeded", await harness.Owner.ScalarAsync<string>("select title from packs where id = @id", ("id", pack)));
    }

    [SkippableFact]
    public async Task Player_owned_packs_are_not_reachable_through_the_admin_routes()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var harness = await StartAsync("editor");
        var player = Guid.NewGuid();
        await harness.Owner.ExecuteAsync("insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @id, 'P', 'p', 'en')", ("tenant", Tenant), ("id", player));
        var pack = await harness.Owner.InsertPackAsync(Tenant, player);

        using var listed = await harness.SendAsync(HttpMethod.Get, "/api/admin/packs");
        using var read = await harness.SendAsync(HttpMethod.Get, $"/api/admin/packs/{pack}");
        using var renamed = await harness.SendAsync(HttpMethod.Put, $"/api/admin/packs/{pack}", JsonContent.Create(new { title = "Taken" }));
        using var deleted = await harness.SendAsync(HttpMethod.Delete, $"/api/admin/packs/{pack}");

        Assert.Empty((await listed.Content.ReadFromJsonAsync<PackResponse[]>())!);
        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound], [read.StatusCode, renamed.StatusCode, deleted.StatusCode]);
        Assert.Equal("Seeded", await harness.Owner.ScalarAsync<string>("select title from packs where id = @id", ("id", pack)));
    }

    [SkippableFact]
    public async Task Pack_questions_stay_out_of_the_default_bank_question_list()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var harness = await StartAsync("editor");
        var bank = await harness.CreateQuestionAsync("Bank question?", "admin");
        var packQuestion = await harness.CreateQuestionAsync("Pack question?", "pack");
        using var created = await harness.SendAsync(HttpMethod.Post, "/api/admin/packs", JsonContent.Create(new { title = "Pack", language = "en" }));
        var pack = (await created.Content.ReadFromJsonAsync<PackResponse>())!;
        await harness.SendAsync(HttpMethod.Post, $"/api/admin/packs/{pack.Id}/questions", JsonContent.Create(new { questionId = packQuestion }));

        using var defaultList = await harness.SendAsync(HttpMethod.Get, "/api/admin/questions");
        using var packList = await harness.SendAsync(HttpMethod.Get, "/api/admin/questions?source=pack");
        var defaults = await defaultList.Content.ReadFromJsonAsync<QuestionListResponse>();
        var explicitPack = await packList.Content.ReadFromJsonAsync<QuestionListResponse>();

        Assert.Equal([bank], defaults!.Items.Select(q => q.Id));
        Assert.Equal(1, defaults.Buckets.Sum(b => b.Approved + b.Pending));
        Assert.Equal([packQuestion], explicitPack!.Items.Select(q => q.Id));
    }

    [SkippableFact]
    public async Task Pack_questions_do_not_make_a_bank_question_a_duplicate()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var harness = await StartAsync("editor");
        await harness.CreateQuestionAsync("Capital of France?", "pack");

        var bank = await harness.CreateQuestionAsync("Capital of France?", "admin");

        Assert.NotEqual(Guid.Empty, bank);
    }

    private async Task<Harness> StartAsync(string role)
    {
        await Quesshi.Server.Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        // TenantDatabaseTests swaps packs for a stand-in table in the shared database; put the real pack tables back.
        if (!await owner.ScalarAsync<bool>("select exists (select 1 from information_schema.columns where table_name = 'packs' and column_name = 'owner_id')"))
        {
            var packs = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly).Single(script => script.Name == "20261024T0100_core_packs.sql");
            await owner.ExecuteAsync("drop table if exists pack_items; drop table if exists packs");
            await owner.ExecuteAsync(System.Text.Encoding.UTF8.GetString(packs.Contents.Span));
        }
        await owner.ExecuteAsync("delete from session_answers; delete from session_items; delete from session_participants; delete from sessions; delete from questions; delete from categories; delete from auth_sessions; delete from admin_memberships; delete from admins; delete from tenant_languages; delete from tenants");
        await owner.ExecuteAsync("insert into tenants (id, slug, name, default_language) values (@t, 'packs-admin', 'Packs', 'en')", ("t", Tenant));
        await owner.ExecuteAsync("insert into tenant_languages (tenant_id, code, name, english_name) values (@t, 'en', 'English', 'English')", ("t", Tenant));
        await owner.ExecuteAsync("insert into tenant_hosts (tenant_id, host) values (@t, @h)", ("t", Tenant), ("h", Host));
        var category = Guid.NewGuid();
        await owner.ExecuteAsync("insert into categories (tenant_id, id, slug, names) values (@t, @id, 'general', '{\"en\":\"General\"}'::jsonb)", ("t", Tenant), ("id", category));
        var admin = Guid.NewGuid();
        await owner.ExecuteAsync("insert into admins (id, username, email, password_hash) values (@id, 'pack-admin', 'pack-admin@example.test', @hash)", ("id", admin), ("hash", new IdentityPasswordHasher().Hash("Strong password 2026!")));
        await owner.ExecuteAsync("insert into admin_memberships (tenant_id, admin_id, role) values (@t, @a, @r)", ("t", Tenant), ("a", admin), ("r", role));
        var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        var app = Quesshi.Server.Program.CreateApplication(sources, TestPorts.Allocate(), TestPorts.Allocate());
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        var context = new DefaultHttpContext();
        Assert.True(await app.Services.GetRequiredService<IAdminSessionIssuer>().IssueAsync(context, Tenant, admin, false));
        var header = Assert.Single(context.Response.Headers.SetCookie.ToArray())!;
        var cookie = Uri.UnescapeDataString(header["__Host-qa=".Length..][..header["__Host-qa=".Length..].IndexOf(';')]);
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var harness = new Harness(app, sources, owner, client, cookie, category);
        harness.Token = (await (await harness.SendAsync(HttpMethod.Get, "/api/admin/antiforgery")).Content.ReadFromJsonAsync<TokenResponse>())!.Token;
        return harness;
    }

    private sealed class Harness(WebApplication app, DatabaseSources sources, NpgsqlConnection owner, HttpClient client, string cookie, Guid category) : IAsyncDisposable
    {
        public NpgsqlConnection Owner { get; } = owner;
        public string Token { get; set; } = string.Empty;

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, bool authenticated = true)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Host = Host;
            if (authenticated) request.Headers.TryAddWithoutValidation("Cookie", $"__Host-qa={cookie}");
            if (authenticated && Token.Length > 0) request.Headers.TryAddWithoutValidation("X-CSRF-Admin", Token);
            if (method != HttpMethod.Get) request.Headers.TryAddWithoutValidation("Origin", $"http://{Host}");
            return await client.SendAsync(request);
        }

        public async Task<Guid> CreateQuestionAsync(string prompt, string source)
        {
            var body = new
            {
                kind = "choice", kindVersion = 1, family = "trivia", language = "en", categoryId = category, level = 1, prompt,
                payload = new { options = new[] { "Paris", "Lyon", "Nice", "Lille" }, correctIndex = 0 },
                topic = (string?)null, status = "approved", source, ownerId = (Guid?)null, mediaId = (Guid?)null,
            };
            using var response = await SendAsync(HttpMethod.Post, "/api/admin/questions", JsonContent.Create(body));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<QuestionResponse>())!.Id;
        }

        public async Task<PackDetailResponse> GetDetailAsync(Guid id)
        {
            using var response = await SendAsync(HttpMethod.Get, $"/api/admin/packs/{id}");
            return (await response.Content.ReadFromJsonAsync<PackDetailResponse>())!;
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            await Owner.DisposeAsync();
            await sources.DisposeAsync();
        }
    }

    private sealed record TokenResponse(string Token);
    private sealed record PackResponse(Guid Id, string Title, string? Description, string Language, string Visibility);
    private sealed record PackQuestionResponse(int Position, Guid Id, string Prompt);
    private sealed record PackDetailResponse(PackResponse Pack, IReadOnlyList<PackQuestionResponse> Questions);
    private sealed record QuestionResponse(Guid Id, JsonElement Payload, string Status, string Prompt);
    private sealed record BucketResponse(int Approved, int Pending);
    private sealed record QuestionListResponse(IReadOnlyList<QuestionResponse> Items, int Total, IReadOnlyList<BucketResponse> Buckets);
}

internal static class OwnerConnectionExtensions
{
    public static async Task ExecuteAsync(this NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(this NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<Guid> InsertPackAsync(this NpgsqlConnection connection, Guid tenant, Guid? owner)
    {
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand("insert into packs (tenant_id, id, owner_id, title, lang) values (@t, @id, @o, 'Seeded', 'en')", connection);
        command.Parameters.AddWithValue("t", tenant);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("o", (object?)owner ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return id;
    }
}
