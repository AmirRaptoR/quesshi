using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Infrastructure.Database;
using Quesshi.Kinds.Choice;
using Quesshi.Plugin.Api;
using Quesshi.Shared;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class QuestionContentDatabaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("d26a0c21-1d32-4db0-a9c0-4537540c7dea");
    private static readonly Guid TenantB = Guid.Parse("bd3a10df-1379-4e9f-8c99-a83a006cb7f2");

    [SkippableFact]
    public async Task Questions_migration_registers_the_envelope_without_media_or_seed_data()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();

        var mediaColumn = await ScalarAsync<long>(owner, "select count(*) from information_schema.columns where table_name = 'questions' and column_name = 'media_id'");

        Assert.Equal(0L, mediaColumn);
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from questions"));
        Assert.Equal("owner:core", await ScalarAsync<string>(owner, "select obj_description('questions'::regclass)"));
    }

    [SkippableFact]
    public async Task Questions_are_isolated_by_tenant_through_the_real_content_service()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        using var app = CreateApplication();
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        _ = await CreateAsync(content, database, TenantA, ValidWrite());

        Assert.Empty(await database.InTenantAsync(TenantB, transaction => content.ListAsync(transaction)));
    }

    [SkippableFact]
    public async Task Deleting_a_question_and_category_nulls_their_session_snapshots()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var player = await InsertPlayerAsync(owner, TenantA);
        var category = await InsertCategoryAsync(owner, TenantA, "session-category");
        var question = await InsertRawQuestionAsync(owner, TenantA, category, 1, "en", "choice", "snapshot question");
        var session = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into sessions (tenant_id, id, code, format, owner_id, lang, capacity, content) values (@tenant, @id, 'snapshot', 'poll', @owner, 'en', 1, '{}'::jsonb)", ("tenant", TenantA), ("id", session), ("owner", player));
        await ExecuteAsync(owner, "insert into session_items (tenant_id, session_id, slot, question_id, kind, kind_version, prompt, payload, card, reveal, category_id) values (@tenant, @session, 0, @question, 'choice', 1, 'snapshot', '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, @category)", ("tenant", TenantA), ("session", session), ("question", question), ("category", category));

        await ExecuteAsync(owner, "delete from questions where tenant_id = @tenant and id = @question; delete from categories where tenant_id = @tenant and id = @category", ("tenant", TenantA), ("question", question), ("category", category));

        Assert.Equal((null, null), await SessionSnapshotReferencesAsync(owner, TenantA, session));
    }

    [SkippableFact]
    public async Task Choice_question_round_trips_create_get_edit_and_status()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        using var app = CreateApplication();
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await CreateAsync(content, database, TenantA, ValidWrite(prompt: "Which city is the capital of France?"));
        var id = created.Question!.Id;
        var edited = await database.InTenantAsync(TenantA, transaction => content.EditAsync(transaction, id, ValidWrite(prompt: "Which city is the capital of France now?", status: QuestionStatus.Approved)));
        var fetched = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, id));

        Assert.Equal(QuestionWriteStatus.Success, created.Status);
        Assert.Equal("trivia", edited.Question!.Family);
        Assert.Equal(1, edited.Question.KindVersion);
        Assert.Equal("Which city is the capital of France now?", fetched!.Prompt);
        Assert.Equal(QuestionStatus.Approved, fetched.Status);
    }

    [SkippableFact]
    public async Task Create_upgrades_an_older_supported_kind_payload_before_storing_it()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        using var app = CreateApplication(new KindRegistry([new VersionTwoChoiceKind()]));
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var result = await CreateAsync(content, database, TenantA, ValidWrite());

        Assert.Equal(2, result.Question!.KindVersion);
    }

    [SkippableFact]
    public async Task Question_status_can_change_without_rewriting_the_envelope()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        using var app = CreateApplication();
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await CreateAsync(content, database, TenantA, ValidWrite());

        var changed = await database.InTenantAsync(TenantA, transaction => content.SetStatusAsync(transaction, created.Question!.Id, QuestionStatus.Approved));

        Assert.True(changed);
    }

    [SkippableFact]
    public async Task Question_can_be_deleted_from_the_content_bank()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        using var app = CreateApplication();
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await CreateAsync(content, database, TenantA, ValidWrite());

        var deleted = await database.InTenantAsync(TenantA, transaction => content.DeleteAsync(transaction, created.Question!.Id));

        Assert.True(deleted);
    }

    [SkippableFact]
    public async Task Malformed_and_multiply_invalid_payloads_do_not_write_or_change_questions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        using var app = CreateApplication();
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await CreateAsync(content, database, TenantA, ValidWrite());
        var malformed = await CreateAsync(content, database, TenantA, ValidWrite(payload: JsonSerializer.SerializeToElement(Array.Empty<string>())));
        var invalid = await database.InTenantAsync(TenantA, transaction => content.EditAsync(transaction, created.Question!.Id, ValidWrite(payload: JsonSerializer.SerializeToElement(new { options = new[] { "", "", "one" }, correctIndex = 9 }))));
        var unchanged = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, created.Question!.Id));

        Assert.Equal(QuestionWriteStatus.Invalid, malformed.Status);
        Assert.True(invalid.Problems!.Count >= 3);
        Assert.Equal("What is the capital of France?", unchanged!.Prompt);
    }

    [SkippableFact]
    public async Task Content_service_accepts_the_16_kb_boundary_and_rejects_a_larger_snapshot()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        using var app = CreateApplication();
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var acceptable = await CreateAsync(content, database, TenantA, ValidWrite(payload: ChoicePayload(8_100)));
        var rejected = await CreateAsync(content, database, TenantA, ValidWrite(prompt: "Which very long option is correct?", payload: ChoicePayload(8_300)));

        Assert.Equal(QuestionWriteStatus.Success, acceptable.Status);
        Assert.Equal(QuestionWriteStatus.Oversize, rejected.Status);
        Assert.Single(await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction)));
    }

    [SkippableFact]
    public async Task Bank_topic_collisions_are_serialized_while_player_owned_questions_are_exempt()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var player = await InsertPlayerAsync(owner, TenantA);
        using var app = CreateApplication();
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var bank = ValidWrite(topic: "france|capital");
        var results = await Task.WhenAll(CreateAsync(content, database, TenantA, bank), CreateAsync(content, database, TenantA, bank with { Prompt = "What city serves as France capital?" }));
        var playerWrite = bank with { Source = QuestionSource.Player, OwnerId = player, Prompt = "What city is France's capital for a player?" };

        Assert.Equal(1, results.Count(result => result.Status == QuestionWriteStatus.Success));
        Assert.Equal(1, results.Count(result => result.Status == QuestionWriteStatus.Duplicate));
        Assert.Equal(QuestionWriteStatus.Success, (await CreateAsync(content, database, TenantA, playerWrite)).Status);
    }

    [SkippableFact]
    public async Task Bank_topic_collisions_allow_another_language_or_family()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "topics");
        const string topic = "france|capital";

        _ = await InsertRawQuestionAsync(owner, TenantA, category, 1, "en", "choice", "English topic", topic: topic);
        var dutch = await InsertRawQuestionAsync(owner, TenantA, category, 1, "nl", "choice", "Dutch topic", topic: topic);
        var otherFamily = await InsertRawQuestionAsync(owner, TenantA, category, 1, "en", "choice", "Poll topic", family: "poll", topic: topic);

        Assert.NotEqual(dutch, otherFamily);
    }

    [SkippableFact]
    public async Task Question_envelope_rejects_a_category_from_another_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var foreignCategory = await InsertCategoryAsync(owner, TenantB, "foreign-category");
        var categoryFailure = await Assert.ThrowsAsync<PostgresException>(() => InsertRawQuestionAsync(owner, TenantA, foreignCategory, 1, "en", "choice", "Wrong category"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, categoryFailure.SqlState);
    }

    [SkippableFact]
    public async Task Question_envelope_rejects_an_owner_from_another_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var foreignPlayer = await InsertPlayerAsync(owner, TenantB);

        var ownerFailure = await Assert.ThrowsAsync<PostgresException>(() => InsertRawQuestionAsync(owner, TenantA, null, 1, "en", "choice", "Wrong owner", source: "player", ownerId: foreignPlayer));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ownerFailure.SqlState);
    }

    [SkippableFact]
    public async Task Listing_filters_all_envelope_fields_and_counts_each_bucket()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var categories = new[]
        {
            await InsertCategoryAsync(owner, TenantA, "first"),
            await InsertCategoryAsync(owner, TenantA, "second"),
            await InsertCategoryAsync(owner, TenantA, "third"),
        };
        for (var level = 1; level <= 5; level++)
        {
            await InsertRawQuestionAsync(owner, TenantA, categories[(level - 1) % categories.Length], level, level % 2 == 0 ? "nl" : "fa", level % 2 == 0 ? "order" : "choice", $"Persian English Dutch search-{level}", level % 2 == 0 ? "approved" : "pending", level % 2 == 0 ? "import" : "admin");
        }
        await InsertRawQuestionAsync(owner, TenantA, categories[2], 1, "en", "choice", "Persian English Dutch search-english", "approved", "import");
        using var app = CreateApplication();
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var filtered = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction, new(Language: "fa", Family: "trivia", Kind: "choice", CategoryId: categories[0], Level: 1, Status: QuestionStatus.Pending, Source: QuestionSource.Admin, Prompt: "persian")));
        var buckets = await database.InTenantAsync(TenantA, transaction => content.BucketCountsAsync(transaction));
        var persian = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction, new(Prompt: "PERSIAN")));
        var english = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction, new(Prompt: "English")));
        var dutch = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction, new(Prompt: "dutch")));

        Assert.Single(filtered);
        Assert.Equal(6, buckets.Count);
        Assert.Equal(1, buckets.Single(bucket => bucket.Language == "fa" && bucket.Level == 1).Pending);
        Assert.Equal(6, persian.Count);
        Assert.Equal(6, english.Count);
        Assert.Equal(6, dutch.Count);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (fixture.ConnectionString is null) return;
        await using var owner = await OpenAsync("quesshi_owner");
        await ResetDatabaseAsync(owner);
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Quesshi.Server.Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = await OpenAsync("quesshi_owner");
        await ResetDatabaseAsync(owner);
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'questions-a', 'Questions A', 'en'), (@b, 'questions-b', 'Questions B', 'en')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@a, 'en', 'English', 'English'), (@a, 'fa', 'فارسی', 'Persian'), (@a, 'nl', 'Nederlands', 'Dutch'), (@b, 'en', 'English', 'English')", ("a", TenantA), ("b", TenantB));
        return owner;
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
    private WebApplication CreateApplication(IKindRegistry? kinds = null) => Quesshi.Server.Program.CreateApplication(Sources(), 11298, 30298, configuredKinds: kinds);
    private static Task ResetDatabaseAsync(NpgsqlConnection owner) => ExecuteAsync(owner, "delete from session_answers; delete from session_items; delete from session_participants; delete from sessions; delete from questions; delete from categories; delete from players; delete from tenant_languages; delete from tenants");
    private async Task<NpgsqlConnection> OpenAsync(string role) { var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role)); await connection.OpenAsync(); return connection; }
    private static QuestionWrite ValidWrite(string prompt = "What is the capital of France?", JsonElement? payload = null, QuestionStatus status = QuestionStatus.Pending, string? topic = null) => new("choice", 1, "trivia", "en", null, 1, prompt, payload ?? ChoicePayload(1), null, topic, status, QuestionSource.Admin, null);
    private static JsonElement ChoicePayload(int firstLength) => JsonSerializer.SerializeToElement(new { options = new[] { new string('a', firstLength), "bravo", "charlie", "delta" }, correctIndex = 0 });
    private static Task<QuestionWriteResult> CreateAsync(IContent content, ITenantDb database, Guid tenant, QuestionWrite write) => database.InTenantAsync(tenant, transaction => content.CreateAsync(transaction, write));
    private static async Task<Guid> InsertCategoryAsync(NpgsqlConnection owner, Guid tenant, string slug) { var id = Guid.NewGuid(); await ExecuteAsync(owner, "insert into categories (tenant_id, id, slug, names) values (@tenant, @id, @slug, '{\"en\":\"Category\"}'::jsonb)", ("tenant", tenant), ("id", id), ("slug", slug)); return id; }
    private static async Task<Guid> InsertPlayerAsync(NpgsqlConnection owner, Guid tenant) { var id = Guid.NewGuid(); await ExecuteAsync(owner, "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @id, 'Player', 'seed', 'en')", ("tenant", tenant), ("id", id)); return id; }
    private static async Task<Guid> InsertRawQuestionAsync(NpgsqlConnection owner, Guid tenant, Guid? category, int level, string language, string kind, string prompt, string status = "pending", string source = "admin", string family = "trivia", string? topic = null, Guid? ownerId = null) { var id = Guid.NewGuid(); await ExecuteAsync(owner, "insert into questions (tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, topic, fingerprint, status, source, owner_id) values (@tenant, @id, @kind, 1, @family, @language, @category, @level, @prompt, '{\"options\":[\"a\",\"b\",\"c\",\"d\"],\"correctIndex\":0}'::jsonb, @topic, @prompt, @status, @source, @owner)", ("tenant", tenant), ("id", id), ("kind", kind), ("family", family), ("language", language), ("category", (object?)category ?? DBNull.Value), ("level", level), ("prompt", prompt), ("topic", (object?)topic ?? DBNull.Value), ("status", status), ("source", source), ("owner", (object?)ownerId ?? DBNull.Value)); return id; }
    private static async Task<(Guid? Question, Guid? Category)> SessionSnapshotReferencesAsync(NpgsqlConnection owner, Guid tenant, Guid session) { await using var command = new NpgsqlCommand("select question_id, category_id from session_items where tenant_id = @tenant and session_id = @session", owner); command.Parameters.AddWithValue("tenant", tenant); command.Parameters.AddWithValue("session", session); await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); return (reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1)); }
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) { await using var command = new NpgsqlCommand(sql, connection); foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value); await command.ExecuteNonQueryAsync(); }
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql) { await using var command = new NpgsqlCommand(sql, connection); var value = (await command.ExecuteScalarAsync())!; return value is T typed ? typed : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture); }

    private sealed class VersionTwoChoiceKind : ChoiceKind
    {
        public override int Version => 2;

        public override ChoicePayload Upgrade(int from, JsonElement payload)
        {
            if (from is not 1 and not 2) throw new NotSupportedException($"choice v{from} cannot be upgraded by choice v2.");
            return JsonSerializer.Deserialize(payload, ChoiceJsonContext.Default.ChoicePayload)
                ?? throw new JsonException("Choice payload is invalid.");
        }
    }
}
