using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Npgsql;
using Quesshi.BankExport;
using BankExportProgram = Quesshi.BankExport.Program;
using Quesshi.Infrastructure.Database;
using Quesshi.Application.Media;
using Quesshi.Infrastructure.Media;
using Quesshi.Infrastructure.Security;
using Quesshi.Server.Security;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AdminQuestionDatabaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("8ab4e15b-45e9-4d67-a32b-b80cbe902c3a");
    private static readonly Guid TenantB = Guid.Parse("54e5eb40-2d22-48a2-9f36-e967f67a1c44");
    private bool databasePrepared;
    private static Uri? clientBaseAddress;

    [SkippableFact]
    public async Task Api_editor_can_create_a_choice_question_and_read_its_payload()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "history");
        var editor = await InsertAdminAsync(owner, "question-editor", "question-editor@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = JsonContent.Create(ChoiceRequest("Which city is the capital of France?", category));
        using var created = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, body);
        var question = await created.Content.ReadFromJsonAsync<QuestionResponse>();
        using var fetched = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{question!.Id}", cookie);
        var reread = await fetched.Content.ReadFromJsonAsync<QuestionResponse>();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(JsonElement.DeepEquals(question.Payload, reread!.Payload));
    }

    [SkippableFact]
    public async Task Api_import_defaults_to_a_dry_run_without_creating_questions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-dry-run");
        var editor = await InsertAdminAsync(owner, "import-dry-run", "import-dry-run@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody(category, "Dry run question");
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        await using var count = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        count.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_commits_row_status_with_import_source()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-commit");
        var editor = await InsertAdminAsync(owner, "import-commit", "import-commit@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody(category, "Committed import question", dryRun: false, status: "rejected");
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        var report = await response.Content.ReadAsStringAsync();
        await using var stored = new NpgsqlCommand("select status || ':' || source from questions where tenant_id = @tenant", owner);
        stored.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((string)(await stored.ExecuteScalarAsync())! == "approved:import", report);
    }

    [SkippableFact]
    public async Task Api_import_resolves_category_slug_and_preserves_legacy_metadata()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "legacy-history");
        var editor = await InsertAdminAsync(owner, "legacy-metadata", "legacy-metadata@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        var row = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                language = "fa",
                categorySlug = "legacy-history",
                level = 3,
                prompt = "Legacy metadata question",
                choice1 = "one",
                choice2 = "two",
                choice3 = "three",
                choice4 = "four",
                correctIndex = 1,
                explanation = "Legacy explanation",
                topic = "legacy-topic",
                status = "approved",
                source = "ai",
            },
        });
        using var body = ImportBody("choice", row, "questions.json", dryRun: false);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        await using var stored = new NpgsqlCommand("""
            select q.lang || ':' || c.slug || ':' || q.level || ':' || q.explanation || ':' || q.topic || ':' || q.status || ':' || q.source
            from questions q join categories c on c.id = q.category_id
            where q.tenant_id = @tenant and q.category_id = @category
            """, owner);
        stored.Parameters.AddWithValue("tenant", TenantA);
        stored.Parameters.AddWithValue("category", category);

        Assert.Equal("fa:legacy-history:3:Legacy explanation:legacy-topic:approved:ai", await stored.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task Legacy_bank_bundle_dry_runs_and_imports_every_question_kind_through_the_platform_apis()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var mongoConnection = Environment.GetEnvironmentVariable("QUESSHI_TEST_MONGO");
        Skip.If(string.IsNullOrWhiteSpace(mongoConnection), "MongoDB is not configured.");
        await using var owner = await PrepareAsync();
        await ExecuteAsync(
            owner,
            "insert into tenant_languages (tenant_id, code, name, english_name) values (@tenant, 'nl', 'Nederlands', 'Dutch')",
            ("tenant", TenantA));
        var editor = await InsertAdminAsync(owner, "bank-import", "bank-import@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        var transport = new ImportMediaTransport(uri => uri.Host == "media.example.test" ? PngResponse(width: 2) : PngResponse());
        await using var sources = Sources();
        await using var app = await StartAsync(sources, services => ConfigureImportMedia(services, transport));
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);

        var root = Path.Combine(Path.GetTempPath(), "quesshi-bank-export-e2e", Guid.NewGuid().ToString("N"));
        var media = Path.Combine(root, "legacy-media");
        var bundle = Path.Combine(root, "bundle");
        var databaseName = "bank_export_e2e_" + Guid.NewGuid().ToString("N");
        var mongo = new MongoClient(mongoConnection);
        try
        {
            Directory.CreateDirectory(media);
            await File.WriteAllBytesAsync(Path.Combine(media, "photo.png"), Png());
            var database = mongo.GetDatabase(databaseName);
            await database.GetCollection<LegacyCategory>("categories").InsertManyAsync(
            [
                new() { Id = "history", NameEn = "History", NameFa = "تاریخ", NameNl = "Geschiedenis", Icon = "book", Color = "#225588", SortOrder = 1, IsActive = true },
                new() { Id = "places", NameEn = "Places", NameFa = "مکان", NameNl = "Plaatsen", Icon = "map", Color = "#228855", SortOrder = 2, IsActive = true },
            ]);
            await database.GetCollection<LegacyContentSettings>("content_settings").InsertOneAsync(new() { TriviaCategoryIds = ["history", "places"], VotingCategoryIds = ["history"] });
            await database.GetCollection<LegacyQuestion>("questions").InsertManyAsync(
            [
                new() { Id = "choice", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Choice", Choices = ["a", "b", "c", "d"], CorrectIndex = 2, MediaUrl = "/media/photo.png", MediaKind = 1, MediaAttribution = "Credit", Explanation = "Choice explanation", Topic = "choice-topic", Status = 1, Source = 1 },
                new() { Id = "missing-media", Family = 0, Lang = 0, CategoryId = "history", Level = 5, Prompt = "Missing media", Choices = ["a", "b", "c", "d"], CorrectIndex = 0, MediaUrl = "/media/missing.png" },
                new() { Id = "order", Family = 0, Kind = 1, Lang = 1, CategoryId = "history", Level = 2, Prompt = "Order", Choices = ["a", "b", "c", "d"], Explanation = "Order explanation", Topic = "order-topic", Status = 2, Source = 2 },
                new() { Id = "map-city", Family = 0, Kind = 2, Lang = 2, CategoryId = "places", Level = 3, Prompt = "Map city", TargetShape = 1, TargetLatitude = 52.3676, TargetLongitude = 4.9041, TargetRadiusKm = 25, BaseLayer = 1 },
                new() { Id = "map-country", Family = 0, Kind = 2, Lang = 1, CategoryId = "places", Level = 4, Prompt = "Map country", TargetShape = 0, TargetCountryCode = "NL", BaseLayer = 0, MediaUrl = "https://media.example.test/country.png", MediaKind = 1, MediaAttribution = "Remote credit" },
                new() { Id = "poll-choice", Family = 1, Lang = 0, CategoryId = "history", Prompt = "Poll choice", AnswerSource = 1, FixedChoices = ["yes", "no"], Explanation = "Poll choice explanation", Topic = "poll-choice-topic" },
                new() { Id = "poll-roster", Family = 1, Lang = 1, CategoryId = "places", Prompt = "Poll roster", AnswerSource = 0, Explanation = "Poll roster explanation", Topic = "poll-roster-topic" },
            ]);

            var exported = await BankExportProgram.Main(["--connection", mongoConnection!, "--database", databaseName, "--output", bundle, "--media-root", media]);
            Assert.Equal(0, exported);
            using var categories = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(bundle, "categories.json")));
            foreach (var category in categories.RootElement.EnumerateArray())
            {
                using var categoryBody = JsonContent.Create(category);
                using var categoryResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/categories", cookie, token, categoryBody);
                Assert.Equal(HttpStatusCode.Created, categoryResponse.StatusCode);
            }
            using var settings = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(bundle, "settings.json")));
            Assert.Equal(["history", "places"], settings.RootElement.GetProperty("triviaCategorySlugs").EnumerateArray().Select(value => value.GetString()!).ToArray());
            Assert.Equal(["history"], settings.RootElement.GetProperty("votingCategorySlugs").EnumerateArray().Select(value => value.GetString()!).ToArray());
            using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(bundle, "report.json")));
            Assert.Equal("missing-media", report.RootElement.GetProperty("missingMedia")[0].GetProperty("questionId").GetString());
            Assert.Equal("/media/missing.png", report.RootElement.GetProperty("missingMedia")[0].GetProperty("path").GetString());
            using var choiceBundle = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(bundle, "choice.json")));
            Assert.Equal("media/missing.png", choiceBundle.RootElement[1].GetProperty("mediaUrl").GetString());

            var mediaBundle = Zip("media/photo.png", Png());
            foreach (var kind in new[] { "choice", "order", "map_pick", "poll_choice", "poll_roster" })
            {
                var rows = await File.ReadAllBytesAsync(Path.Combine(bundle, kind + ".json"));
                using var dryRunBody = ImportBody(kind, rows, kind + ".json", bundle: mediaBundle);
                using var dryRun = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, dryRunBody);
                using var dryRunReport = await ReportAsync(dryRun);
                if (kind == "choice")
                {
                    await using var dryRunCount = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
                    dryRunCount.Parameters.AddWithValue("tenant", TenantA);
                    Assert.Equal(0L, (long)(await dryRunCount.ExecuteScalarAsync())!);
                }
                using var importBody = ImportBody(kind, rows, kind + ".json", dryRun: false, bundle: mediaBundle);
                using var imported = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, importBody);
                using var importReport = await ReportAsync(imported);
                var expectedRows = kind switch
                {
                    "choice" => new[] { "would_import", "media_bundle_invalid" },
                    "map_pick" => new[] { "would_import", "would_import" },
                    _ => new[] { "would_import" },
                };
                var expectedImports = expectedRows.Select(outcome => outcome == "would_import" ? "imported" : outcome).ToArray();
                Assert.Equal(expectedRows, Outcomes(dryRunReport));
                Assert.Equal(expectedImports, Outcomes(importReport));
            }
            await using var importedCount = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
            importedCount.Parameters.AddWithValue("tenant", TenantA);
            Assert.Equal(6L, (long)(await importedCount.ExecuteScalarAsync())!);
            var importedRows = await ImportedRowsAsync(owner);
            var choice = importedRows["Choice"];
            Assert.Equal(["a", "b", "c", "d"], choice.Payload.GetProperty("options").EnumerateArray().Select(option => option.GetString()!).ToArray());
            Assert.Equal(2, choice.Payload.GetProperty("correctIndex").GetInt32());
            Assert.Equal("fa:history:1:Choice explanation:choice-topic:approved:ai:Credit:image", choice.Metadata);
            var order = importedRows["Order"];
            Assert.Equal(["a", "b", "c", "d"], order.Payload.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("text").GetString()!).ToArray());
            Assert.Equal("en:history:2:Order explanation:order-topic:rejected:admin::", order.Metadata);
            var city = importedRows["Map city"].Payload;
            Assert.Equal("Borders", city.GetProperty("baseLayer").GetString());
            Assert.Equal(52.3676, city.GetProperty("target").GetProperty("latitude").GetDouble());
            Assert.Equal(4.9041, city.GetProperty("target").GetProperty("longitude").GetDouble());
            Assert.Equal(25, city.GetProperty("target").GetProperty("radiusKm").GetDouble());
            var country = importedRows["Map country"];
            Assert.Equal("Blank", country.Payload.GetProperty("baseLayer").GetString());
            Assert.Equal("Country", country.Payload.GetProperty("target").GetProperty("shape").GetString());
            Assert.Equal("NL", country.Payload.GetProperty("target").GetProperty("countryCode").GetString());
            Assert.Equal("en:places:4:::pending:seed:Remote credit:image", country.Metadata);
            Assert.Equal(["yes", "no"], importedRows["Poll choice"].Payload.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("text").GetString()!).ToArray());
            Assert.Equal("fa:history::Poll choice explanation:poll-choice-topic:pending:seed::", importedRows["Poll choice"].Metadata);
            Assert.Equal(JsonValueKind.Object, importedRows["Poll roster"].Payload.ValueKind);
            Assert.Equal("en:places::Poll roster explanation:poll-roster-topic:pending:seed::", importedRows["Poll roster"].Metadata);
        }
        finally
        {
            await mongo.DropDatabaseAsync(databaseName);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [SkippableFact]
    public async Task Api_import_commits_pending_when_status_is_omitted()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-default-status");
        var editor = await InsertAdminAsync(owner, "import-default-status", "import-default-status@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody(category, "Pending import question", dryRun: false, rowStatus: null);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        await using var stored = new NpgsqlCommand("select status || ':' || source from questions where tenant_id = @tenant", owner);
        stored.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal("pending:import", (string)(await stored.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_requires_an_authenticated_editor()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-editor-only");
        var moderator = await InsertAdminAsync(owner, "import-moderator", "import-moderator@example.test");
        await MembershipAsync(owner, TenantA, moderator, "moderator");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        using var anonymousBody = ImportBody(category, "Anonymous import", dryRun: false);
        using var anonymous = await SendAsync(client, HttpMethod.Post, "/api/admin/import", content: anonymousBody);
        var moderatorCookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, moderator);
        var moderatorToken = await AdminAntiforgeryAsync(client, moderatorCookie);
        using var moderatorBody = ImportBody(category, "Moderator import", dryRun: false);
        using var forbidden = await SendAsync(client, HttpMethod.Post, "/api/admin/import", moderatorCookie, moderatorToken, moderatorBody);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [SkippableFact]
    public async Task Api_import_templates_require_an_editor_and_a_known_kind_and_format()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        _ = await InsertCategoryAsync(owner, TenantA, "import-template-permissions");
        var moderator = await InsertAdminAsync(owner, "template-moderator", "template-moderator@example.test");
        await MembershipAsync(owner, TenantA, moderator, "moderator");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        using var anonymous = await SendAsync(client, HttpMethod.Get, "/api/admin/import/templates/choice.csv");
        var moderatorCookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, moderator);
        using var forbidden = await SendAsync(client, HttpMethod.Get, "/api/admin/import/templates/choice.csv", moderatorCookie);
        var editor = await InsertAdminAsync(owner, "template-editor", "template-editor@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        var editorCookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        using var unknownKind = await SendAsync(client, HttpMethod.Get, "/api/admin/import/templates/unknown.csv", editorCookie);
        using var unknownFormat = await SendAsync(client, HttpMethod.Get, "/api/admin/import/templates/choice.txt", editorCookie);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownFormat.StatusCode);
    }

    [SkippableFact]
    public async Task Api_import_requires_admin_antiforgery()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-antiforgery");
        var editor = await InsertAdminAsync(owner, "import-antiforgery", "import-antiforgery@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        using var body = ImportBody(category, "Unprotected import", dryRun: false);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, content: body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Api_import_rejects_an_invalid_admin_antiforgery_token()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-invalid-antiforgery");
        var editor = await InsertAdminAsync(owner, "import-invalid-antiforgery", "import-invalid-antiforgery@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        using var body = ImportBody(category, "Invalid antiforgery import", dryRun: false);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, "invalid-token", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Api_import_rejects_a_category_from_another_tenant_without_writing_it()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var foreignCategory = await InsertCategoryAsync(owner, TenantB, "import-foreign-category");
        var editor = await InsertAdminAsync(owner, "import-tenant-isolation", "import-tenant-isolation@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody(foreignCategory, "Foreign category import", dryRun: false);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        await using var count = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        count.Parameters.AddWithValue("tenant", TenantB);

        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_stores_a_committed_question_only_in_the_requesting_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-requesting-tenant");
        var editor = await InsertAdminAsync(owner, "import-requesting-tenant", "import-requesting-tenant@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody(category, "Requesting tenant import", dryRun: false);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        await using var inTenantA = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        inTenantA.Parameters.AddWithValue("tenant", TenantA);
        await using var inTenantB = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        inTenantB.Parameters.AddWithValue("tenant", TenantB);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1L, (long)(await inTenantA.ExecuteScalarAsync())!);
        Assert.Equal(0L, (long)(await inTenantB.ExecuteScalarAsync())!);
    }

    [SkippableTheory]
    [InlineData("choice", "csv")]
    [InlineData("choice", "json")]
    [InlineData("order", "csv")]
    [InlineData("order", "json")]
    [InlineData("map_pick", "csv")]
    [InlineData("map_pick", "json")]
    [InlineData("poll_choice", "csv")]
    [InlineData("poll_choice", "json")]
    [InlineData("poll_roster", "csv")]
    [InlineData("poll_roster", "json")]
    public async Task Api_import_template_can_be_downloaded_and_committed(string kind, string format)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        _ = await InsertCategoryAsync(owner, TenantA, "import-template");
        var editor = await InsertAdminAsync(owner, $"template-{kind}-{format}", $"template-{kind}-{format}@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var template = await SendAsync(client, HttpMethod.Get, $"/api/admin/import/templates/{kind}.{format}", cookie);
        var bytes = await template.Content.ReadAsByteArrayAsync();
        using var dryRunBody = ImportBody(kind, bytes, $"{kind}.{format}");
        using var dryRun = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, dryRunBody);
        using var dryRunReport = await ReportAsync(dryRun);
        using var commitBody = ImportBody(kind, bytes, $"{kind}.{format}", dryRun: false);
        using var imported = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, commitBody);
        using var importedReport = await ReportAsync(imported);

        Assert.Equal(HttpStatusCode.OK, template.StatusCode);
        Assert.Equal(["would_import"], Outcomes(dryRunReport));
        Assert.Equal(["imported"], Outcomes(importedReport));
    }

    [SkippableFact]
    public async Task Api_import_rejects_request_boundaries_before_writing_questions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-boundaries");
        var editor = await InsertAdminAsync(owner, "import-boundaries", "import-boundaries@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var unknownKindBody = ImportBody("unknown", ChoiceCsv(category, ("Unknown kind", 1, null, null, null, null, null)), "questions.csv");
        using var unknownKind = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, unknownKindBody);
        using var unknownFormatBody = ImportBody("choice", ChoiceCsv(category, ("Unknown format", 1, null, null, null, null, null)), "questions.txt");
        using var unknownFormat = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, unknownFormatBody);
        using var emptyBody = ImportBody("choice", [], "questions.csv");
        using var empty = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, emptyBody);
        using var missingHeaderBody = ImportBody("choice", "prompt\nNo headers\n"u8.ToArray(), "questions.csv");
        using var missingHeader = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, missingHeaderBody);
        using var malformedJsonBody = ImportBody("choice", "[{"u8.ToArray(), "questions.json");
        using var malformedJson = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, malformedJsonBody);
        await using var count = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        count.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownFormat.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingHeader.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformedJson.StatusCode);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_requires_each_common_csv_header_before_writing_questions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-common-headers");
        var editor = await InsertAdminAsync(owner, "import-common-headers", "import-common-headers@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        var headers = new[]
        {
            ("choice", "category,level,prompt,choice1,choice2,choice3,choice4,correctIndex"),
            ("choice", "language,level,prompt,choice1,choice2,choice3,choice4,correctIndex"),
            ("choice", "language,category,prompt,choice1,choice2,choice3,choice4,correctIndex"),
            ("poll_roster", "category,prompt,answerSource"),
            ("poll_roster", "language,prompt,answerSource"),
        };
        var responses = new List<HttpResponseMessage>();
        foreach (var (kind, header) in headers)
        {
            var csv = header + "\nen," + category + ",1,Missing common header,one,two,three,four,1\n";
            responses.Add(await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, ImportBody(kind, System.Text.Encoding.UTF8.GetBytes(csv), "questions.csv")));
        }
        await using var count = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        count.Parameters.AddWithValue("tenant", TenantA);
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [SkippableFact]
    public async Task Api_import_preserves_valid_rows_after_malformed_csv_and_json_rows()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-row-continuation");
        var editor = await InsertAdminAsync(owner, "import-row-continuation", "import-row-continuation@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        var valid = "en," + category + ",1,First,one,two,three,four,1\n";
        var malformed = "malformed\n";
        var second = "en," + category + ",2,Second,one,two,three,four,1\n";
        using var csvBody = ImportBody("choice", System.Text.Encoding.UTF8.GetBytes("language,category,level,prompt,choice1,choice2,choice3,choice4,correctIndex\n" + valid + malformed + second), "questions.csv", dryRun: false);
        using var csv = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, csvBody);
        using var csvReport = await ReportAsync(csv);
        var json = "[{\"language\":\"en\",\"category\":\"" + category + "\",\"level\":3,\"prompt\":\"Third\",\"choice1\":\"one\",\"choice2\":\"two\",\"choice3\":\"three\",\"choice4\":\"four\",\"correctIndex\":1},4,{\"language\":\"en\",\"category\":\"" + category + "\",\"level\":4,\"prompt\":\"Fourth\",\"choice1\":\"one\",\"choice2\":\"two\",\"choice3\":\"three\",\"choice4\":\"four\",\"correctIndex\":1}]";
        using var jsonBody = ImportBody("choice", System.Text.Encoding.UTF8.GetBytes(json), "questions.json", dryRun: false);
        using var jsonResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, jsonBody);
        using var jsonReport = await ReportAsync(jsonResponse);

        Assert.Equal(["imported", "bad_row", "imported"], Outcomes(csvReport));
        Assert.Equal(["imported", "bad_row", "imported"], Outcomes(jsonReport));
    }

    [SkippableFact]
    public async Task Api_import_accepts_case_insensitive_csv_fields_and_legacy_kind_aliases()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-legacy-fields");
        var editor = await InsertAdminAsync(owner, "import-legacy-fields", "import-legacy-fields@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        var csv = "LANGUAGE,CATEGORY,LEVEL,PROMPT,ITEM1,ITEM2,ITEM3,ITEM4\nen," + category + ",1,\"Legacy, order\",one,two,three,four\n";
        using var sortBody = ImportBody("sort", System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(), "questions.csv", dryRun: false);
        using var sort = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, sortBody);
        using var sortReport = await ReportAsync(sort);
        var mapJson = "[{\"LANGUAGE\":\"en\",\"CATEGORY\":\"" + category + "\",\"LEVEL\":1,\"PROMPT\":\"Legacy map\",\"TARGETSHAPE\":\"country\",\"COUNTRYCODE\":\"NL\",\"BASELAYER\":\"borders\"}]";
        using var mapBody = ImportBody("map", System.Text.Encoding.UTF8.GetBytes(mapJson), "questions.json", dryRun: false);
        using var map = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, mapBody);
        using var mapReport = await ReportAsync(map);

        Assert.Equal(["imported"], Outcomes(sortReport));
        Assert.Equal(["imported"], Outcomes(mapReport));
    }

    [SkippableFact]
    public async Task Api_import_preserves_bom_quoted_newlines_and_ignored_csv_columns_through_the_http_route()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-csv-compatibility");
        var editor = await InsertAdminAsync(owner, "import-csv-compatibility", "import-csv-compatibility@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        var csv = "LANGUAGE,CATEGORY,LEVEL,PROMPT,CHOICE1,CHOICE2,CHOICE3,CHOICE4,CORRECTINDEX,IGNORED\n" +
                  "en," + category + ",1,\"First line\nSecond line\",one,two,three,four,1,ignored\n";
        using var body = ImportBody("choice", System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(), "questions.csv", dryRun: false);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        using var report = await ReportAsync(response);
        await using var prompt = new NpgsqlCommand("select prompt from questions where tenant_id = @tenant", owner);
        prompt.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(["imported"], Outcomes(report));
        Assert.Equal("First line\nSecond line", (string)(await prompt.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_rejects_row_and_size_limits_before_writing_questions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-limits");
        var editor = await InsertAdminAsync(owner, "import-limits", "import-limits@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        var line = "en," + category + ",1,Question,one,two,three,four,1\n";
        var rows = "language,category,level,prompt,choice1,choice2,choice3,choice4,correctIndex\n" + string.Concat(Enumerable.Repeat(line, 2_001));
        using var rowBody = ImportBody("choice", System.Text.Encoding.UTF8.GetBytes(rows), "questions.csv");
        using var tooManyRows = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, rowBody);
        using var sizeBody = ImportBody("choice", new byte[20 * 1024 * 1024 + 1], "questions.csv");
        using var tooLarge = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, sizeBody);
        await using var count = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        count.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(HttpStatusCode.BadRequest, tooManyRows.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_reports_duplicate_invalid_and_valid_rows_independently()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-partial");
        var editor = await InsertAdminAsync(owner, "import-partial", "import-partial@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var originalBody = ImportBody("choice", ChoiceCsv(category, ("Existing prompt", 1, null, null, null, "Europe", "capital")), "questions.csv", dryRun: false);
        using var original = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, originalBody);
        using var originalReport = await ReportAsync(original);
        using var body = ImportBody("choice", ChoiceCsv(category,
            ("Existing prompt", 1, null, null, null, "Other", "topic"),
            ("Not reserved", 9, null, null, null, "Other", "invalid"),
            ("Not reserved", 2, null, null, null, "One", "one"),
            ("Valid two", 3, null, null, null, "Two", "two"),
            ("Valid three", 4, null, null, null, "Three", "three")), "questions.csv", dryRun: false);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        using var report = await ReportAsync(response);
        await using var count = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        count.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(["imported"], Outcomes(originalReport));
        Assert.Equal(["duplicate", "invalid", "imported", "imported", "imported"], Outcomes(report));
        Assert.Equal(
            originalReport.RootElement.GetProperty("rows")[0].GetProperty("questionId").GetString(),
            report.RootElement.GetProperty("rows")[0].GetProperty("existingQuestionId").GetString());
        Assert.Equal(4L, (long)(await count.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_reports_tenant_topic_and_in_file_duplicates_at_the_import_boundary()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-duplicate-boundaries");
        var editor = await InsertAdminAsync(owner, "import-duplicate-boundaries", "import-duplicate-boundaries@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var unsupportedBody = ImportBody("choice", System.Text.Encoding.UTF8.GetBytes("language,category,level,prompt,choice1,choice2,choice3,choice4,correctIndex\nzz," + category + ",1,Unsupported language,one,two,three,four,1\n"), "questions.csv", dryRun: false);
        using var unsupported = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, unsupportedBody);
        using var unsupportedReport = await ReportAsync(unsupported);
        using var existingBody = ImportBody("choice", ChoiceCsv(category, ("Existing topic", 1, null, null, null, "Europe", "capital")), "questions.csv", dryRun: false);
        using var existing = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, existingBody);
        using var existingReport = await ReportAsync(existing);
        using var topicBody = ImportBody("choice", ChoiceCsv(category, ("Different prompt", 2, null, null, null, "Europe", "capital")), "questions.csv", dryRun: false);
        using var topic = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, topicBody);
        using var topicReport = await ReportAsync(topic);
        using var fileBody = ImportBody("choice", ChoiceCsv(category,
            ("File duplicate one", 3, null, null, null, "File", "same"),
            ("File duplicate two", 4, null, null, null, "File", "same")), "questions.csv", dryRun: false);
        using var file = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, fileBody);
        using var fileReport = await ReportAsync(file);

        Assert.Equal(["invalid"], Outcomes(unsupportedReport));
        Assert.Equal(["duplicate"], Outcomes(topicReport));
        Assert.Equal(existingReport.RootElement.GetProperty("rows")[0].GetProperty("questionId").GetString(), topicReport.RootElement.GetProperty("rows")[0].GetProperty("existingQuestionId").GetString());
        Assert.Equal(["imported", "duplicate"], Outcomes(fileReport));
        Assert.Equal(1, fileReport.RootElement.GetProperty("rows")[1].GetProperty("existingRow").GetInt32());
    }

    [SkippableFact]
    public async Task Api_import_concurrent_commits_report_one_import_and_one_duplicate()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-race");
        var editor = await InsertAdminAsync(owner, "import-race", "import-race@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using (var hold = new NpgsqlCommand("""
            create function hold_prompt_fingerprint_import() returns trigger language plpgsql as $$
            begin
              if new.prompt = 'Competing question' then perform pg_sleep(0.4); end if;
              return new;
            end;
            $$;
            create trigger hold_prompt_fingerprint_import before insert on questions
            for each row execute function hold_prompt_fingerprint_import();
            """, owner))
        {
            await hold.ExecuteNonQueryAsync();
        }

        try
        {
            await using var sources = Sources();
            await using var app = await StartAsync(sources);
            using var client = Client();
            var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
            var token = await AdminAntiforgeryAsync(client, cookie);
            using var firstBody = ImportBody("choice", ChoiceCsv(category, ("Competing question", 1, null, null, null, null, null)), "questions.csv", dryRun: false);
            using var secondBody = ImportBody("choice", ChoiceCsv(category, ("Competing question", 1, null, null, null, null, null)), "questions.csv", dryRun: false);
            var responses = await Task.WhenAll(
                SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, firstBody),
                SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, secondBody));
            using var first = responses[0];
            using var second = responses[1];
            using var firstReport = await ReportAsync(first);
            using var secondReport = await ReportAsync(second);
            await using var count = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
            count.Parameters.AddWithValue("tenant", TenantA);
            var duplicate = new[] { firstReport, secondReport }.SelectMany(report => report.RootElement.GetProperty("rows").EnumerateArray()).Single(row => row.GetProperty("outcome").GetString() == "duplicate");

            Assert.Equal(["duplicate", "imported"], Outcomes(firstReport).Concat(Outcomes(secondReport)).Order().ToArray());
            Assert.False(string.IsNullOrWhiteSpace(duplicate.GetProperty("existingQuestionId").GetString()));
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("drop trigger if exists hold_prompt_fingerprint_import on questions; drop function if exists hold_prompt_fingerprint_import();", owner);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task Api_import_stores_an_attributed_bundle_asset_and_rejects_unsafe_bundle_paths()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-bundle");
        var editor = await InsertAdminAsync(owner, "import-bundle", "import-bundle@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var badBody = ImportBody("choice", ChoiceCsv(category, ("Unsafe bundle", 1, "../server-file.png", "image", "No read", null, null)), "questions.csv", dryRun: false, bundle: Zip("../server-file.png", Png()));
        using var bad = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, badBody);
        using var badReport = await ReportAsync(bad);
        using var validBody = ImportBody("choice", ChoiceCsv(category, ("Bundled media", 2, "images/photo.png", "image", "Bundle credit", null, null)), "questions.csv", dryRun: false, bundle: Zip("images/photo.png", Png()));
        using var valid = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, validBody);
        await using var attribution = new NpgsqlCommand("select m.attribution from questions q join media_assets m on m.tenant_id = q.tenant_id and m.id = q.media_id where q.tenant_id = @tenant", owner);
        attribution.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(["media_bundle_invalid"], Outcomes(badReport));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("Bundle credit", (string)(await attribution.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_rejects_a_symbolic_link_bundle_entry_without_creating_a_question()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-bundle-symlink");
        var editor = await InsertAdminAsync(owner, "import-bundle-symlink", "import-bundle-symlink@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody("choice", ChoiceCsv(category, ("Linked bundle", 1, "media.png", "image", null, null, null)), "questions.csv", dryRun: false, bundle: Zip("media.png", Png(), 0xa000 << 16));
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        using var report = await ReportAsync(response);
        await using var count = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        count.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(["media_bundle_invalid"], Outcomes(report));
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_rejects_missing_absolute_and_missing_bundle_entries_without_questions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-bundle-boundaries");
        var editor = await InsertAdminAsync(owner, "import-bundle-boundaries", "import-bundle-boundaries@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var noBundleBody = ImportBody("choice", ChoiceCsv(category, ("No bundle", 1, "media.png", "image", null, null, null)), "questions.csv", dryRun: false);
        using var noBundle = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, noBundleBody);
        using var noBundleReport = await ReportAsync(noBundle);
        using var absoluteBody = ImportBody("choice", ChoiceCsv(category, ("Absolute", 1, "/media.png", "image", null, null, null)), "questions.csv", dryRun: false, bundle: Zip("media.png", Png()));
        using var absolute = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, absoluteBody);
        using var absoluteReport = await ReportAsync(absolute);
        using var missingBody = ImportBody("choice", ChoiceCsv(category, ("Missing", 1, "missing.png", "image", null, null, null)), "questions.csv", dryRun: false, bundle: Zip("present.png", Png()));
        using var missing = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, missingBody);
        using var missingReport = await ReportAsync(missing);
        await using var count = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        count.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(["media_bundle_invalid"], Outcomes(noBundleReport));
        Assert.Equal(["media_unreachable"], Outcomes(absoluteReport));
        Assert.Equal(["media_bundle_invalid"], Outcomes(missingReport));
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_fetches_checked_url_media_with_attribution_and_continues_after_failure()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-url-media");
        var editor = await InsertAdminAsync(owner, "import-url-media", "import-url-media@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        var transport = new ImportMediaTransport(uri => uri.Host switch
        {
            "missing.example" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => PngResponse(),
        });
        await using var sources = Sources();
        await using var app = await StartAsync(sources, services => ConfigureImportMedia(services, transport));
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody("choice", ChoiceCsv(category,
            ("Missing media", 1, "https://missing.example/photo.png", "image", "No asset", null, null),
            ("Fetched media", 2, "https://media.example/photo.png", "image", "URL credit", null, null)), "questions.csv", dryRun: false);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        using var report = await ReportAsync(response);
        await using var attribution = new NpgsqlCommand("select m.attribution from questions q join media_assets m on m.tenant_id = q.tenant_id and m.id = q.media_id where q.tenant_id = @tenant", owner);
        attribution.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(["media_unreachable", "imported"], Outcomes(report));
        Assert.Equal("URL credit", (string)(await attribution.ExecuteScalarAsync())!);
        Assert.Equal(2, transport.Calls);
    }

    [SkippableFact]
    public async Task Api_import_rejects_a_url_redirect_to_a_forbidden_destination_without_a_question()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-url-redirect");
        var editor = await InsertAdminAsync(owner, "import-url-redirect", "import-url-redirect@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        var transport = new ImportMediaTransport(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://forbidden.example/image.png") } });
        await using var sources = Sources();
        await using var app = await StartAsync(sources, services => ConfigureImportMedia(services, transport));
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody("choice", ChoiceCsv(category, ("Forbidden redirect", 1, "https://redirect.example/image.png", "image", null, null, null)), "questions.csv", dryRun: false);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        using var report = await ReportAsync(response);
        await using var count = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        count.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(["media_unreachable"], Outcomes(report));
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_dry_run_reports_media_without_writing_questions_or_assets()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-media-dry-run");
        var editor = await InsertAdminAsync(owner, "import-media-dry-run", "import-media-dry-run@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        var transport = new ImportMediaTransport(_ => PngResponse());
        await using var sources = Sources();
        await using var app = await StartAsync(sources, services => ConfigureImportMedia(services, transport));
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody("choice", ChoiceCsv(category, ("Dry media", 1, "https://media.example/photo.png", "image", "Dry credit", null, null)), "questions.csv");
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        using var report = await ReportAsync(response);
        await using var questions = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        questions.Parameters.AddWithValue("tenant", TenantA);
        await using var assets = new NpgsqlCommand("select count(*) from media_assets where tenant_id = @tenant", owner);
        assets.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(["would_import"], Outcomes(report));
        Assert.Equal(0L, (long)(await questions.ExecuteScalarAsync())!);
        Assert.Equal(0L, (long)(await assets.ExecuteScalarAsync())!);
        Assert.Equal(1, transport.Calls);
    }

    [SkippableFact]
    public async Task Api_import_dry_run_and_commit_have_equivalent_stable_row_reports_without_dry_run_writes_or_body_fetches()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-stable-dry-run");
        var editor = await InsertAdminAsync(owner, "import-stable-dry-run", "import-stable-dry-run@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        var bodyReads = 0;
        var transport = new ImportMediaTransport(_ => PngResponse(() => bodyReads++));
        await using var sources = Sources();
        await using var app = await StartAsync(sources, services => ConfigureImportMedia(services, transport));
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var existingBody = ImportBody("choice", ChoiceCsv(category, ("Stable existing", 1, null, null, null, null, null)), "questions.csv", dryRun: false);
        using var existing = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, existingBody);
        var csv = ChoiceCsv(category,
            ("Stable existing", 1, null, null, null, null, null),
            ("Stable invalid", 9, null, null, null, null, null),
            ("Stable media", 2, "https://media.example/photo.png", "image", "Stable credit", null, null));
        using var dryRunBody = ImportBody("choice", csv, "questions.csv");
        using var dryRun = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, dryRunBody);
        using var dryRunReport = await ReportAsync(dryRun);
        await using var beforeQuestions = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        beforeQuestions.Parameters.AddWithValue("tenant", TenantA);
        await using var beforeAssets = new NpgsqlCommand("select count(*) from media_assets where tenant_id = @tenant", owner);
        beforeAssets.Parameters.AddWithValue("tenant", TenantA);
        var questionsAfterDryRun = (long)(await beforeQuestions.ExecuteScalarAsync())!;
        var assetsAfterDryRun = (long)(await beforeAssets.ExecuteScalarAsync())!;
        var callsAfterDryRun = transport.Calls;
        var bodyReadsAfterDryRun = bodyReads;
        using var commitBody = ImportBody("choice", csv, "questions.csv", dryRun: false);
        using var commit = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, commitBody);
        using var commitReport = await ReportAsync(commit);

        Assert.Equal(["duplicate", "invalid", "would_import"], Outcomes(dryRunReport));
        Assert.Equal(["duplicate", "invalid", "imported"], Outcomes(commitReport));
        Assert.Equal(RowProblems(dryRunReport), RowProblems(commitReport));
        Assert.Equal(1L, questionsAfterDryRun);
        Assert.Equal(0L, assetsAfterDryRun);
        Assert.Equal(0, bodyReadsAfterDryRun);
        Assert.Equal(1, callsAfterDryRun);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(1, bodyReads);
    }

    [SkippableFact]
    public async Task Api_import_reports_nonblocking_level_and_answer_length_warnings()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-warnings");
        var editor = await InsertAdminAsync(owner, "import-warnings", "import-warnings@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        var csv = "language,category,level,prompt,choice1,choice2,choice3,choice4,correctIndex\n" +
            "en," + category + ",1,Length tell,tiny,medium,very-long-answer,small,2\n";
        using var body = ImportBody("choice", System.Text.Encoding.UTF8.GetBytes(csv), "questions.csv");
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        using var report = await ReportAsync(response);
        var warnings = report.RootElement.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetProperty("code").GetString()).ToArray();

        Assert.Equal(1, report.RootElement.GetProperty("accepted").GetInt32());
        Assert.Contains("levels.incomplete", warnings);
        Assert.Contains("choice.answer_length_tell", warnings);
    }

    [SkippableFact]
    public async Task Api_import_reports_a_shortest_correct_answer_length_warning()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-shortest-warning");
        var editor = await InsertAdminAsync(owner, "import-shortest-warning", "import-shortest-warning@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        var csv = "language,category,level,prompt,choice1,choice2,choice3,choice4,correctIndex\n" +
                  "en," + category + ",1,Shortest tell,longer,medium,tiny,wide,2\n";
        using var body = ImportBody("choice", System.Text.Encoding.UTF8.GetBytes(csv), "questions.csv");
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        using var report = await ReportAsync(response);

        Assert.Contains(report.RootElement.GetProperty("warnings").EnumerateArray(), warning => warning.GetProperty("code").GetString() == "choice.answer_length_tell");
    }

    [SkippableFact]
    public async Task Api_import_splits_tied_answer_lengths_at_the_warning_threshold()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-tied-warning");
        var editor = await InsertAdminAsync(owner, "import-tied-warning", "import-tied-warning@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        var rows = Enumerable.Range(1, 4).Select(level => "en," + category + "," + level + ",Tied " + level + ",tiny,wide,longer,longer,2")
            .Append("en," + category + ",5,Neither,tiny,wide,middle,very-long,2");
        var csv = "language,category,level,prompt,choice1,choice2,choice3,choice4,correctIndex\n" + string.Join("\n", rows) + "\n";
        using var body = ImportBody("choice", System.Text.Encoding.UTF8.GetBytes(csv), "questions.csv");
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        using var report = await ReportAsync(response);

        Assert.DoesNotContain(report.RootElement.GetProperty("warnings").EnumerateArray(), warning => warning.GetProperty("code").GetString() == "choice.answer_length_tell");
    }

    [SkippableFact]
    public async Task Api_import_does_not_report_quality_warnings_for_a_balanced_complete_file()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-clean-warnings");
        var editor = await InsertAdminAsync(owner, "import-clean-warnings", "import-clean-warnings@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody("choice", ChoiceCsv(category,
            ("Clean one", 1, null, null, null, null, null),
            ("Clean two", 2, null, null, null, null, null),
            ("Clean three", 3, null, null, null, null, null),
            ("Clean four", 4, null, null, null, null, null),
            ("Clean five", 5, null, null, null, null, null)), "questions.csv");
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        using var report = await ReportAsync(response);

        Assert.Equal(5, report.RootElement.GetProperty("accepted").GetInt32());
        Assert.Empty(report.RootElement.GetProperty("warnings").EnumerateArray());
    }

    [SkippableFact]
    public async Task Api_editor_uploads_tenant_media_with_attribution_and_public_delivery_is_isolated()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var editor = await InsertAdminAsync(owner, "media-editor", "media-editor@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        MediaResponse? asset = null;

        try
        {
            using var uploadBody = UploadBody(Png(), "image/png", "Photo credit");
            using var uploaded = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, uploadBody);
            asset = await uploaded.Content.ReadFromJsonAsync<MediaResponse>();
            using var delivered = await SendAsync(client, HttpMethod.Get, $"/media/{asset!.Hash}");
            using var foreign = await SendAsync(client, HttpMethod.Get, $"/media/{asset.Hash}", host: "questions-b.example.test");

            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            Assert.Equal("Photo credit", asset.Attribution);
            Assert.Equal("image/png", delivered.Content.Headers.ContentType!.MediaType);
            Assert.Equal("public, max-age=31536000, immutable", delivered.Headers.CacheControl!.ToString());
            Assert.Equal(Png(), await delivered.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }
        finally
        {
            if (asset is not null)
                await app.Services.GetRequiredService<IMediaStore>().DeleteAsync(TenantA, asset.Id);
        }
    }

    [SkippableFact]
    public async Task Api_media_upload_requires_editor_and_antiforgery_and_audio_supports_ranges()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var editor = await InsertAdminAsync(owner, "range-editor", "range-editor@example.test");
        var moderator = await InsertAdminAsync(owner, "range-moderator", "range-moderator@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await MembershipAsync(owner, TenantA, moderator, "moderator");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var editorCookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var moderatorCookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, moderator);
        var editorToken = await AdminAntiforgeryAsync(client, editorCookie);
        var moderatorToken = await AdminAntiforgeryAsync(client, moderatorCookie);
        MediaResponse? asset = null;

        try
        {
            using var moderatorBody = UploadBody(Wav(), "audio/wav");
            using var moderatorResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", moderatorCookie, moderatorToken, moderatorBody);
            using var anonymousBody = UploadBody(Wav(), "audio/wav");
            using var anonymousResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", content: anonymousBody);
            using var missingTokenBody = UploadBody(Wav(), "audio/wav");
            using var missingTokenResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", editorCookie, content: missingTokenBody);
            using var validBody = UploadBody(Wav(), "audio/wav");
            using var uploaded = await SendAsync(client, HttpMethod.Post, "/api/admin/media", editorCookie, editorToken, validBody);
            asset = await uploaded.Content.ReadFromJsonAsync<MediaResponse>();

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/media/{asset!.Hash}");
            rangeRequest.Headers.Host = "questions-a.example.test";
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 7);
            using var range = await client.SendAsync(rangeRequest);
            using var unsatisfiableRequest = new HttpRequestMessage(HttpMethod.Get, $"/media/{asset.Hash}");
            unsatisfiableRequest.Headers.Host = "questions-a.example.test";
            unsatisfiableRequest.Headers.Range = new RangeHeaderValue(10_000, null);
            using var unsatisfiable = await client.SendAsync(unsatisfiableRequest);

            Assert.Equal(HttpStatusCode.Forbidden, moderatorResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, missingTokenResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal("bytes 0-7/48", range.Content.Headers.ContentRange!.ToString());
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);
        }
        finally
        {
            if (asset is not null)
                await app.Services.GetRequiredService<IMediaStore>().DeleteAsync(TenantA, asset.Id);
        }
    }

    [SkippableFact]
    public async Task Api_media_upload_rejects_size_limits_and_type_mismatches_without_assets()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var editor = await InsertAdminAsync(owner, "limits-editor", "limits-editor@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        var root = Path.Combine(Path.GetTempPath(), "quesshi-media-upload-rejections", Guid.NewGuid().ToString("N"));
        var priorRoot = Environment.GetEnvironmentVariable("MEDIA_DIR");
        Environment.SetEnvironmentVariable("MEDIA_DIR", root);
        try
        {
            await using var sources = Sources();
            await using var app = await StartAsync(sources);
            using var client = Client();
            var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
            var token = await AdminAntiforgeryAsync(client, cookie);

            using var imageOverLimit = UploadBody(new byte[5 * 1024 * 1024 + 1], "image/png");
            using var imageResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, imageOverLimit);
            using var audioOverLimit = UploadBody(new byte[10 * 1024 * 1024 + 1], "audio/wav");
            using var audioResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, audioOverLimit);
            using var videoOverLimit = UploadBody(new byte[25 * 1024 * 1024 + 1], "video/mp4");
            using var videoResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, videoOverLimit);
            using var mismatch = UploadBody(Png(), "image/jpeg");
            using var mismatchResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, mismatch);
            using var unsupported = UploadBody(Png(), "image/gif");
            using var unsupportedResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, unsupported);
            using var unrecognised = UploadBody("not a media file"u8.ToArray(), "image/png");
            using var unrecognisedResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, unrecognised);
            await using var count = new NpgsqlCommand("select count(*) from media_assets where tenant_id = @tenant", owner);
            count.Parameters.AddWithValue("tenant", TenantA);

            Assert.Equal(HttpStatusCode.BadRequest, imageResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, audioResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, videoResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, mismatchResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, unsupportedResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, unrecognisedResponse.StatusCode);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEDIA_DIR", priorRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Api_questions_attach_and_return_only_media_owned_by_the_resolved_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "media-attachment");
        var editor = await InsertAdminAsync(owner, "media-question-editor", "media-question-editor@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        var createdMedia = await InsertMediaAsync(owner, TenantA);
        var editedMedia = await InsertMediaAsync(owner, TenantA);
        var foreignMedia = await InsertMediaAsync(owner, TenantB);
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var createBody = JsonContent.Create(ChoiceRequest("Media create", category, mediaId: createdMedia));
        using var created = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, createBody);
        var question = await created.Content.ReadFromJsonAsync<QuestionResponse>();
        using var editBody = JsonContent.Create(ChoiceRequest("Media edit", category, mediaId: editedMedia));
        using var edited = await SendAsync(client, HttpMethod.Put, $"/api/admin/questions/{question!.Id}", cookie, token, editBody);
        var updated = await edited.Content.ReadFromJsonAsync<QuestionResponse>();
        using var fetched = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{question.Id}", cookie);
        var reread = await fetched.Content.ReadFromJsonAsync<QuestionResponse>();
        using var listed = await SendAsync(client, HttpMethod.Get, "/api/admin/questions", cookie);
        var page = await listed.Content.ReadFromJsonAsync<QuestionListResponse>();
        using var foreignBody = JsonContent.Create(ChoiceRequest("Foreign media", category, mediaId: foreignMedia));
        using var foreign = await SendAsync(client, HttpMethod.Put, $"/api/admin/questions/{question.Id}", cookie, token, foreignBody);
        using var unknownBody = JsonContent.Create(ChoiceRequest("Unknown media", category, mediaId: Guid.NewGuid()));
        using var unknown = await SendAsync(client, HttpMethod.Put, $"/api/admin/questions/{question.Id}", cookie, token, unknownBody);
        using var unchanged = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{question.Id}", cookie);
        var afterRejectedEdits = await unchanged.Content.ReadFromJsonAsync<QuestionResponse>();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(createdMedia, question.MediaId);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(editedMedia, updated!.MediaId);
        Assert.Equal(editedMedia, reread!.MediaId);
        Assert.Equal(editedMedia, Assert.Single(page!.Items).MediaId);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(editedMedia, afterRejectedEdits!.MediaId);
    }

    [SkippableFact]
    public async Task Api_editor_can_update_list_and_delete_questions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "geography");
        var editor = await InsertAdminAsync(owner, "question-crud", "question-crud@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var createBody = JsonContent.Create(ChoiceRequest("Original capital question", category));
        using var created = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, createBody);
        var id = (await created.Content.ReadFromJsonAsync<QuestionResponse>())!.Id;
        using var updateBody = JsonContent.Create(ChoiceRequest("Updated capital question", category, status: "approved"));
        using var updated = await SendAsync(client, HttpMethod.Put, $"/api/admin/questions/{id}", cookie, token, updateBody);
        using var secondBody = JsonContent.Create(ChoiceRequest("Second capital question", category, status: "approved"));
        using var second = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, secondBody);
        using var listed = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions?language=en&family=trivia&kind=choice&categoryId={category}&level=1&status=approved&source=admin&text=Updated&page=1&pageSize=1", cookie);
        var page = await listed.Content.ReadFromJsonAsync<QuestionListResponse>();
        using var paged = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions?language=en&family=trivia&kind=choice&categoryId={category}&level=1&status=approved&source=admin&page=2&pageSize=1", cookie);
        var secondPage = await paged.Content.ReadFromJsonAsync<QuestionListResponse>();
        using var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/admin/questions/{id}", cookie, token);
        using var missing = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{id}", cookie);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(1, page!.Total);
        Assert.Equal("Updated capital question", Assert.Single(page.Items).Prompt);
        Assert.Equal(1, Assert.Single(page.Buckets).Approved);
        Assert.Equal("Second capital question", Assert.Single(secondPage!.Items).Prompt);
        Assert.Equal(2, Assert.Single(secondPage.Buckets).Approved);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [SkippableFact]
    public async Task Api_editor_and_owner_can_set_approve_and_reject_question_status()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "status");
        var editor = await InsertAdminAsync(owner, "question-status-editor", "question-status-editor@example.test");
        var tenantOwner = await InsertAdminAsync(owner, "question-status-owner", "question-status-owner@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await MembershipAsync(owner, TenantA, tenantOwner, "owner");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
        var editorCookie = await IssueCookieAsync(sessions, TenantA, editor);
        var ownerCookie = await IssueCookieAsync(sessions, TenantA, tenantOwner);
        var editorToken = await AdminAntiforgeryAsync(client, editorCookie);
        var ownerToken = await AdminAntiforgeryAsync(client, ownerCookie);
        using var createBody = JsonContent.Create(ChoiceRequest("Status question", category));
        using var created = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", editorCookie, editorToken, createBody);
        var id = (await created.Content.ReadFromJsonAsync<QuestionResponse>())!.Id;
        using var approve = await SendAsync(client, HttpMethod.Post, $"/api/admin/questions/{id}/approve", editorCookie, editorToken);
        using var reject = await SendAsync(client, HttpMethod.Post, $"/api/admin/questions/{id}/reject", ownerCookie, ownerToken);
        using var statusBody = JsonContent.Create(new { status = "draft" });
        using var setStatus = await SendAsync(client, HttpMethod.Put, $"/api/admin/questions/{id}/status", editorCookie, editorToken, statusBody);
        using var read = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{id}", editorCookie);
        var question = await read.Content.ReadFromJsonAsync<QuestionResponse>();

        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, setStatus.StatusCode);
        Assert.Equal("draft", question!.Status);
    }

    [SkippableFact]
    public async Task Api_question_writes_refuse_moderators_anonymous_and_unsafe_requests()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "security");
        var editor = await InsertAdminAsync(owner, "question-security-editor", "question-security-editor@example.test");
        var moderator = await InsertAdminAsync(owner, "question-security-moderator", "question-security-moderator@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await MembershipAsync(owner, TenantA, moderator, "moderator");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
        var editorCookie = await IssueCookieAsync(sessions, TenantA, editor);
        var moderatorCookie = await IssueCookieAsync(sessions, TenantA, moderator);
        var moderatorToken = await AdminAntiforgeryAsync(client, moderatorCookie);
        using var moderatorBody = JsonContent.Create(ChoiceRequest("Moderator cannot write", category));
        using var moderatorResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", moderatorCookie, moderatorToken, moderatorBody);
        using var anonymousBody = JsonContent.Create(ChoiceRequest("Anonymous cannot write", category));
        using var anonymousResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", content: anonymousBody);
        using var missingTokenBody = JsonContent.Create(ChoiceRequest("Missing token", category));
        using var missingToken = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", editorCookie, content: missingTokenBody);
        var editorToken = await AdminAntiforgeryAsync(client, editorCookie);
        using var wrongOriginBody = JsonContent.Create(ChoiceRequest("Wrong origin", category));
        using var wrongOrigin = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", editorCookie, editorToken, wrongOriginBody, origin: "http://foreign.example.test");

        Assert.Equal(HttpStatusCode.Forbidden, moderatorResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);
    }

    [SkippableFact]
    public async Task Api_question_validation_and_duplicate_errors_preserve_the_bank()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "validation");
        var editor = await InsertAdminAsync(owner, "question-validation", "question-validation@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var invalidBody = JsonContent.Create(new { kind = "choice", kindVersion = 1, family = "trivia", language = "en", categoryId = category, level = 1, prompt = "Invalid", payload = new { options = new[] { "only" }, correctIndex = -1 }, status = "pending", source = "admin" });
        using var invalid = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, invalidBody);
        var invalidError = await invalid.Content.ReadFromJsonAsync<ErrorResponse>();
        var large = Enumerable.Range(0, 4).Select(index => new string((char)('a' + index), 5000)).ToArray();
        using var oversizeBody = JsonContent.Create(ChoiceRequest("Too large", category, payload: new { options = large, correctIndex = 0 }));
        using var oversize = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, oversizeBody);
        var oversizeError = await oversize.Content.ReadFromJsonAsync<ErrorResponse>();
        using var firstTopicBody = JsonContent.Create(ChoiceRequest("Topic original", category, topic: "bank-topic"));
        using var firstTopic = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, firstTopicBody);
        using var duplicateTopicBody = JsonContent.Create(ChoiceRequest("Topic duplicate", category, topic: "bank-topic"));
        using var duplicateTopic = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, duplicateTopicBody);
        var duplicateTopicError = await duplicateTopic.Content.ReadFromJsonAsync<ErrorResponse>();
        using var fingerprintBody = JsonContent.Create(ChoiceRequest("Fingerprint original", category));
        using var fingerprint = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, fingerprintBody);
        using var duplicateFingerprintBody = JsonContent.Create(ChoiceRequest("Fingerprint original", category));
        using var duplicateFingerprint = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, duplicateFingerprintBody);
        var duplicateFingerprintError = await duplicateFingerprint.Content.ReadFromJsonAsync<ErrorResponse>();
        using var listed = await SendAsync(client, HttpMethod.Get, "/api/admin/questions", cookie);
        var page = await listed.Content.ReadFromJsonAsync<QuestionListResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains(invalidError!.Problems!, problem => problem.Code == "options.tooFew");
        Assert.Contains(invalidError.Problems!, problem => problem.Code == "correctIndex.outOfRange");
        Assert.Equal(HttpStatusCode.BadRequest, oversize.StatusCode);
        Assert.Equal("question_too_large", oversizeError!.Error);
        Assert.Equal(HttpStatusCode.Created, firstTopic.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicateTopic.StatusCode);
        Assert.Equal("duplicate_question", duplicateTopicError!.Error);
        Assert.Equal(HttpStatusCode.Created, fingerprint.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicateFingerprint.StatusCode);
        Assert.Equal("duplicate_question", duplicateFingerprintError!.Error);
        Assert.Equal(2, page!.Total);
    }

    [SkippableFact]
    public async Task Api_question_envelope_and_cross_tenant_boundaries_are_client_errors()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var categoryA = await InsertCategoryAsync(owner, TenantA, "boundary-a");
        var categoryB = await InsertCategoryAsync(owner, TenantB, "boundary-b");
        var playerB = await InsertPlayerAsync(owner, TenantB);
        var editorA = await InsertAdminAsync(owner, "question-boundary-a", "question-boundary-a@example.test");
        var editorB = await InsertAdminAsync(owner, "question-boundary-b", "question-boundary-b@example.test");
        await MembershipAsync(owner, TenantA, editorA, "editor");
        await MembershipAsync(owner, TenantB, editorB, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
        var cookieA = await IssueCookieAsync(sessions, TenantA, editorA);
        var cookieB = await IssueCookieAsync(sessions, TenantB, editorB);
        var tokenA = await AdminAntiforgeryAsync(client, cookieA);
        var tokenB = await AdminAntiforgeryAsync(client, cookieB, "questions-b.example.test");
        using var unknownKindBody = JsonContent.Create(ChoiceRequest("Unknown kind", categoryA, kind: "unknown"));
        using var unknownKind = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, unknownKindBody);
        using var invalidLanguageBody = JsonContent.Create(ChoiceRequest("Invalid language", categoryA, language: "zz"));
        using var invalidLanguage = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, invalidLanguageBody);
        using var foreignCategoryBody = JsonContent.Create(ChoiceRequest("Foreign category", categoryB));
        using var foreignCategory = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, foreignCategoryBody);
        using var foreignOwnerBody = JsonContent.Create(ChoiceRequest("Foreign owner", categoryA, source: "player", ownerId: playerB));
        using var foreignOwner = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, foreignOwnerBody);
        using var invalidStatusBody = JsonContent.Create(ChoiceRequest("Invalid status", categoryA, status: "unknown"));
        using var invalidStatus = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, invalidStatusBody);
        using var invalidLevelBody = JsonContent.Create(new { kind = "choice", kindVersion = 1, family = "trivia", language = "en", categoryId = categoryA, level = 0, prompt = "Invalid level", payload = new { options = new[] { "A", "B", "C", "D" }, correctIndex = 0 }, status = "pending", source = "admin" });
        using var invalidLevel = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, invalidLevelBody);
        using var invalidSourceBody = JsonContent.Create(new { kind = "choice", kindVersion = 1, family = "trivia", language = "en", categoryId = categoryA, level = 1, prompt = "Invalid source", payload = new { options = new[] { "A", "B", "C", "D" }, correctIndex = 0 }, status = "pending", source = "unknown" });
        using var invalidSource = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, invalidSourceBody);
        using var foreignCreateBody = JsonContent.Create(ChoiceRequest("Foreign question", categoryB));
        using var foreignCreate = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieB, tokenB, foreignCreateBody, host: "questions-b.example.test");
        var foreignId = (await foreignCreate.Content.ReadFromJsonAsync<QuestionResponse>())!.Id;
        using var foreignRead = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{foreignId}", cookieA);
        using var missingRead = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{Guid.NewGuid()}", cookieA);

        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLanguage.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignCategory.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignOwner.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLevel.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidSource.StatusCode);
        Assert.Equal(HttpStatusCode.Created, foreignCreate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRead.StatusCode);
    }

    [SkippableFact]
    public async Task Api_category_delete_protects_referenced_questions_and_isolates_tenants()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var unused = await InsertCategoryAsync(owner, TenantA, "unused");
        var referenced = await InsertCategoryAsync(owner, TenantA, "referenced");
        var pollReferenced = await InsertCategoryAsync(owner, TenantA, "poll-referenced");
        var foreign = await InsertCategoryAsync(owner, TenantB, "foreign");
        var editorA = await InsertAdminAsync(owner, "question-category-a", "question-category-a@example.test");
        var editorB = await InsertAdminAsync(owner, "question-category-b", "question-category-b@example.test");
        await MembershipAsync(owner, TenantA, editorA, "editor");
        await MembershipAsync(owner, TenantB, editorB, "editor");
        await InsertPollQuestionAsync(owner, TenantA, pollReferenced);
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
        var cookieA = await IssueCookieAsync(sessions, TenantA, editorA);
        var tokenA = await AdminAntiforgeryAsync(client, cookieA);
        using var createBody = JsonContent.Create(ChoiceRequest("Referenced category", referenced));
        using var create = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, createBody);
        using var deletedUnused = await SendAsync(client, HttpMethod.Delete, $"/api/admin/categories/{unused}", cookieA, tokenA);
        using var refused = await SendAsync(client, HttpMethod.Delete, $"/api/admin/categories/{referenced}", cookieA, tokenA);
        var refusedError = await refused.Content.ReadFromJsonAsync<ErrorResponse>();
        using var pollRefused = await SendAsync(client, HttpMethod.Delete, $"/api/admin/categories/{pollReferenced}", cookieA, tokenA);
        var pollRefusedError = await pollRefused.Content.ReadFromJsonAsync<ErrorResponse>();
        using var deactivated = await SendAsync(client, HttpMethod.Post, $"/api/admin/categories/{referenced}/deactivate", cookieA, tokenA);
        using var foreignDelete = await SendAsync(client, HttpMethod.Delete, $"/api/admin/categories/{foreign}", cookieA, tokenA);

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deletedUnused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("category_in_use", refusedError!.Error);
        Assert.Equal(HttpStatusCode.BadRequest, pollRefused.StatusCode);
        Assert.Equal("category_in_use", pollRefusedError!.Error);
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (fixture.ConnectionString is null || !databasePrepared) return;
        await using var owner = await OpenAsync("quesshi_owner");
        await ResetDatabaseAsync(owner);
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        databasePrepared = true;
        await Quesshi.Server.Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = await OpenAsync("quesshi_owner");
        await ResetDatabaseAsync(owner);
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'questions-a', 'Questions A', 'en'), (@b, 'questions-b', 'Questions B', 'en')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@a, 'en', 'English', 'English'), (@a, 'fa', 'فارسی', 'Persian'), (@b, 'en', 'English', 'English')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_hosts (tenant_id, host) values (@a, 'questions-a.example.test'), (@b, 'questions-b.example.test')", ("a", TenantA), ("b", TenantB));
        return owner;
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static Task ResetDatabaseAsync(NpgsqlConnection owner) =>
        ExecuteAsync(owner, "delete from session_answers; delete from session_items; delete from session_participants; delete from sessions; delete from questions; delete from categories; delete from auth_sessions; delete from admin_memberships; delete from admins; delete from tenant_languages; delete from tenants");

    private static async Task<WebApplication> StartAsync(DatabaseSources sources, Action<IServiceCollection>? configureServices = null)
    {
        var app = Quesshi.Server.Program.CreateApplication(sources, 11299, 30299, configureServices: configureServices);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        clientBaseAddress = new Uri(app.Urls.Single());
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        return app;
    }

    private static async Task<IReadOnlyDictionary<string, ImportedBankRow>> ImportedRowsAsync(NpgsqlConnection owner)
    {
        await using var command = new NpgsqlCommand("""
            select q.prompt, q.payload::text,
                coalesce(q.lang, '') || ':' || c.slug || ':' || coalesce(q.level::text, '') || ':' ||
                coalesce(q.explanation, '') || ':' || coalesce(q.topic, '') || ':' || q.status || ':' || q.source || ':' ||
                coalesce(m.attribution, '') || ':' || coalesce(m.kind, '')
            from questions q
            join categories c on c.tenant_id = q.tenant_id and c.id = q.category_id
            left join media_assets m on m.tenant_id = q.tenant_id and m.id = q.media_id
            where q.tenant_id = @tenant
            """, owner);
        command.Parameters.AddWithValue("tenant", TenantA);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new Dictionary<string, ImportedBankRow>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            using var payload = JsonDocument.Parse(reader.GetString(1));
            rows.Add(reader.GetString(0), new ImportedBankRow(payload.RootElement.Clone(), reader.GetString(2)));
        }
        return rows;
    }

    private sealed record ImportedBankRow(JsonElement Payload, string Metadata);

    private static HttpClient Client() => new() { BaseAddress = clientBaseAddress ?? throw new InvalidOperationException("The test application has not started.") };

    private static object ChoiceRequest(string prompt, Guid category, string status = "pending", string? topic = null, string kind = "choice", string language = "en", object? payload = null, string source = "admin", Guid? ownerId = null, Guid? mediaId = null) => new
    {
        kind, kindVersion = 1, family = "trivia", language, categoryId = category, level = 1, prompt,
        payload = payload ?? new { options = new[] { "Paris", "Lyon", "Nice", "Lille" }, correctIndex = 0 },
        topic, status, source, ownerId, mediaId,
    };

    private static MultipartFormDataContent UploadBody(byte[] bytes, string mime, string? attribution = null)
    {
        var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        body.Add(file, "file", "sample");
        if (attribution is not null) body.Add(new StringContent(attribution), "attribution");
        return body;
    }

    private static MultipartFormDataContent ImportBody(Guid category, string prompt, bool? dryRun = null, string? status = null, string? rowStatus = "approved")
    {
        var body = new MultipartFormDataContent();
        body.Add(new StringContent("choice"), "kind");
        if (dryRun is not null) body.Add(new StringContent(dryRun.Value.ToString().ToLowerInvariant()), "dryRun");
        if (status is not null) body.Add(new StringContent(status), "status");
        var csv = $"language,category,level,prompt,choice1,choice2,choice3,choice4,correctIndex,status\nen,{category},2,\"{prompt}\",Paris,Lyon,Nice,Lille,0,{rowStatus}\n";
        body.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "questions.csv");
        return body;
    }

    private static MultipartFormDataContent ImportBody(string kind, byte[] bytes, string fileName, bool? dryRun = null, string? status = null, byte[]? bundle = null)
    {
        var body = new MultipartFormDataContent();
        body.Add(new StringContent(kind), "kind");
        if (dryRun is not null) body.Add(new StringContent(dryRun.Value.ToString().ToLowerInvariant()), "dryRun");
        if (status is not null) body.Add(new StringContent(status), "status");
        body.Add(new ByteArrayContent(bytes), "file", fileName);
        if (bundle is not null) body.Add(new ByteArrayContent(bundle), "mediaBundle", "media.zip");
        return body;
    }

    private static byte[] ChoiceCsv(Guid category, params (string Prompt, int Level, string? MediaUrl, string? MediaKind, string? Attribution, string? Subject, string? Aspect)[] rows)
    {
        const string header = "language,category,level,prompt,choice1,choice2,choice3,choice4,correctIndex,mediaUrl,mediaKind,mediaAttribution,subject,aspect";
        var values = rows.Select(row => string.Join(',', [
            "en", category.ToString(), row.Level.ToString(), CsvValue(row.Prompt), "short", "medium", "very-long", "tiny", "1",
            CsvValue(row.MediaUrl), CsvValue(row.MediaKind), CsvValue(row.Attribution), CsvValue(row.Subject), CsvValue(row.Aspect)]));
        return System.Text.Encoding.UTF8.GetBytes(header + "\n" + string.Join("\n", values) + "\n");
    }

    private static string CsvValue(string? value) => value is null ? string.Empty : '"' + value.Replace("\"", "\"\"") + '"';

    private static byte[] Zip(string name, byte[] contents, int externalAttributes = 0)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(name);
            entry.ExternalAttributes = externalAttributes;
            using var output = entry.Open();
            output.Write(contents);
        }
        return stream.ToArray();
    }

    private static async Task<JsonDocument> ReportAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

    private static string[] Outcomes(JsonDocument report) =>
        report.RootElement.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("outcome").GetString()!).ToArray();

    private static string[] RowProblems(JsonDocument report) =>
        report.RootElement.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("problems").GetRawText())
            .ToArray();

    private static void ConfigureImportMedia(IServiceCollection services, ImportMediaTransport transport)
    {
        services.RemoveAll<IMediaFetcher>();
        services.AddSingleton<IMediaFetcher>(provider => new MediaFetcher(
            new ImportMediaResolver(),
            provider.GetRequiredService<IMediaStore>(),
            transport));
    }

    private static HttpResponseMessage PngResponse(Action? bodyRead = null, byte width = 1)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = bodyRead is null ? new ByteArrayContent(Png(width)) : new TrackingContent(Png(width), bodyRead),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }

    private sealed class TrackingContent(byte[] bytes, Action bodyRead) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            bodyRead();
            await stream.WriteAsync(bytes);
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            bodyRead();
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class ImportMediaResolver : IHostAddressResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("8.8.8.8")]);
    }

    private sealed class ImportMediaTransport(Func<Uri, HttpResponseMessage> response) : IMediaFetchTransport
    {
        public int Calls { get; private set; }

        public Task<MediaFetchResponse> GetAsync(Uri uri, IPAddress address, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new MediaFetchResponse(new HttpClient(), response(uri)));
        }
    }

    private static byte[] Png(byte width = 1) =>
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
        0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, width, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00,
    ];

    private static byte[] Wav() =>
    [
        0x52, 0x49, 0x46, 0x46, 0x28, 0x00, 0x00, 0x00,
        0x57, 0x41, 0x56, 0x45, 0x66, 0x6d, 0x74, 0x20,
        0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
        0x40, 0x1f, 0x00, 0x00, 0x80, 0x3e, 0x00, 0x00,
        0x02, 0x00, 0x10, 0x00, 0x64, 0x61, 0x74, 0x61,
        0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    private static async Task<Guid> InsertCategoryAsync(NpgsqlConnection owner, Guid tenant, string slug)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into categories (tenant_id, id, slug, names) values (@tenant, @id, @slug, '{\"en\":\"Category\"}'::jsonb)", ("tenant", tenant), ("id", id), ("slug", slug));
        return id;
    }

    private static Task InsertPollQuestionAsync(NpgsqlConnection owner, Guid tenant, Guid category) =>
        ExecuteAsync(owner, "insert into questions (tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source) values (@tenant, @id, 'poll_choice', 1, 'poll', 'en', @category, 1, 'Poll category reference', '{\"options\":[\"yes\",\"no\"]}'::jsonb, 'poll-category-reference', 'pending', 'admin')", ("tenant", tenant), ("id", Guid.NewGuid()), ("category", category));

    private static async Task<Guid> InsertMediaAsync(NpgsqlConnection owner, Guid tenant)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into media_assets (tenant_id, id, kind, storage_key, mime, bytes) values (@tenant, @id, 'image', @key, 'image/png', 1)", ("tenant", tenant), ("id", id), ("key", id.ToString("N")));
        return id;
    }

    private static async Task<Guid> InsertPlayerAsync(NpgsqlConnection owner, Guid tenant)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @id, 'Question owner', 'question-owner', 'en')", ("tenant", tenant), ("id", id));
        return id;
    }

    private static async Task<Guid> InsertAdminAsync(NpgsqlConnection owner, string username, string email)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into admins (id, username, email, password_hash) values (@id, @username, @email, @hash)", ("id", id), ("username", username), ("email", email), ("hash", new IdentityPasswordHasher().Hash("Strong password 2026!")));
        return id;
    }

    private static Task MembershipAsync(NpgsqlConnection owner, Guid tenant, Guid admin, string role) =>
        ExecuteAsync(owner, "insert into admin_memberships (tenant_id, admin_id, role) values (@tenant, @admin, @role)", ("tenant", tenant), ("admin", admin), ("role", role));

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> IssueCookieAsync(IAdminSessionIssuer sessions, Guid tenant, Guid admin)
    {
        var context = new DefaultHttpContext();
        Assert.True(await sessions.IssueAsync(context, tenant, admin, false));
        var header = Assert.Single(context.Response.Headers.SetCookie.ToArray())
            ?? throw new InvalidOperationException("The admin session cookie was missing.");
        var value = header["__Host-qa=".Length..] ?? throw new InvalidOperationException("The admin session cookie was malformed.");
        return Uri.UnescapeDataString(value[..value.IndexOf(';')]);
    }

    private static async Task<string> AdminAntiforgeryAsync(HttpClient client, string cookie, string host = "questions-a.example.test")
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/api/admin/antiforgery", cookie, host: host);
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.Token;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? cookie = null, string? antiforgery = null, HttpContent? content = null, string host = "questions-a.example.test", string? origin = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Host = host;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"__Host-qa={cookie}");
        if (antiforgery is not null) request.Headers.TryAddWithoutValidation("X-CSRF-Admin", antiforgery);
        if (method != HttpMethod.Get) request.Headers.TryAddWithoutValidation("Origin", origin ?? $"http://{host}");
        return await client.SendAsync(request);
    }

    private sealed record AntiforgeryResponse(string Token);
    private sealed record MediaResponse(Guid Id, string Hash, string Kind, string Mime, long Bytes, JsonElement Meta, string? Attribution);
    private sealed record QuestionResponse(Guid Id, JsonElement Payload, string Status, string Prompt, Guid? MediaId);
    private sealed record QuestionListResponse(IReadOnlyList<QuestionResponse> Items, int Total, IReadOnlyList<QuestionBucketResponse> Buckets);
    private sealed record QuestionBucketResponse(int Approved, int Pending);
    private sealed record ErrorResponse(string Error, IReadOnlyList<ContentProblemResponse>? Problems);
    private sealed record ContentProblemResponse(string Code);
}
