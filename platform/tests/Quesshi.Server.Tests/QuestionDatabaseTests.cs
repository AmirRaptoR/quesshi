using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;
using Quesshi.Shared;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class QuestionDatabaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("03d19c24-9139-4b31-a609-7d9a5af25364");
    private static readonly Guid TenantB = Guid.Parse("af7320b8-d2c4-46ee-b6d6-5d917be55fcf");
    private static readonly Guid CategoryA = Guid.Parse("e2b8cb9c-3cff-47d8-ab70-b3498bc52002");
    private static readonly Guid CategoryB = Guid.Parse("c84315ec-a529-4cb5-8de7-624814ff2599");
    private static readonly Guid CategoryC = Guid.Parse("d82d7c57-4d3f-466a-a07d-a6d3e6d9e779");

    [SkippableFact]
    public async Task Questions_migration_registers_an_empty_tenant_table_with_session_item_foreign_keys()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();

        var result = await ScalarAsync<bool>(owner, """
            select
              (select count(*) = 0 from questions)
              and exists (select 1 from pg_constraint where conname = 'session_items_question_fk' and confdeltype = 'n')
              and exists (select 1 from pg_constraint where conname = 'session_items_category_fk' and confdeltype = 'n')
              and obj_description('questions'::regclass) = 'owner:core'
            """);

        Assert.True(result);
    }

    [SkippableFact]
    public async Task Deleting_a_question_or_category_nulls_only_its_session_item_reference()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var player = Guid.NewGuid();
        var session = Guid.NewGuid();
        var question = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @player, 'Owner', 'seed', 'en')", ("tenant", TenantA), ("player", player));
        await InsertCategoryAsync(owner, TenantA, CategoryA, "session-item-category");
        await ExecuteAsync(owner, "insert into sessions (tenant_id, id, code, format, owner_id, lang, capacity, content) values (@tenant, @session, 'QFK001', 'poll', @player, 'en', 1, '{}'::jsonb)", ("tenant", TenantA), ("session", session), ("player", player));
        await InsertQuestionAsync(owner, TenantA, question, "choice", "trivia", "en", CategoryA, 2, "Stored question", "approved", "admin", "session-item-question");
        await ExecuteAsync(owner, "insert into session_items (tenant_id, session_id, slot, question_id, kind, kind_version, prompt, payload, card, reveal, category_id) values (@tenant, @session, 0, @question, 'choice', 1, 'Stored question', '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, @category)", ("tenant", TenantA), ("session", session), ("question", question), ("category", CategoryA));
        await ExecuteAsync(owner, "delete from questions where tenant_id = @tenant and id = @question", ("tenant", TenantA), ("question", question));
        var questionReferenceOnlyWasCleared = await ScalarAsync<bool>(owner, "select question_id is null and category_id = @category from session_items where tenant_id = @tenant and session_id = @session", ("tenant", TenantA), ("session", session), ("category", CategoryA));
        await ExecuteAsync(owner, "delete from categories where tenant_id = @tenant and id = @category", ("tenant", TenantA), ("category", CategoryA));

        var categoryReferenceWasCleared = await ScalarAsync<bool>(owner, "select question_id is null and category_id is null from session_items where tenant_id = @tenant and session_id = @session", ("tenant", TenantA), ("session", session));

        Assert.True(questionReferenceOnlyWasCleared && categoryReferenceWasCleared);
    }

    [SkippableFact]
    public async Task Questions_are_isolated_between_tenants()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("Tenant A")));
        if (created.Question is null) throw new InvalidOperationException("Question creation failed.");

        var questions = await database.InTenantAsync(TenantB, transaction => content.ListAsync(transaction, new QuestionFilter()));

        Assert.Empty(questions);
    }

    [SkippableFact]
    public async Task Choice_question_round_trips_its_payload_and_current_kind_version()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var write = ValidQuestion("Which city is France's capital?");
        var created = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, write));
        var id = created.Question?.Id ?? throw new InvalidOperationException("Question creation failed.");

        var stored = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, id));

        Assert.True(stored is { Family: "trivia", KindVersion: 1 } && JsonElement.DeepEquals(write.Payload, stored.Payload));
    }

    [SkippableFact]
    public async Task Editing_a_choice_question_preserves_the_replacement_payload()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("First prompt")));
        var id = created.Question?.Id ?? throw new InvalidOperationException("Question creation failed.");
        var changed = ValidQuestion("Updated prompt", "{\"options\":[\"Rome\",\"Paris\",\"Madrid\",\"Berlin\"],\"correctIndex\":1}");
        _ = await database.InTenantAsync(TenantA, transaction => content.EditAsync(transaction, id, changed));

        var stored = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, id));

        Assert.True(stored is not null && JsonElement.DeepEquals(changed.Payload, stored.Payload));
    }

    [SkippableFact]
    public async Task Status_changes_and_deletion_are_persisted_through_the_content_contract()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("Lifecycle prompt")));
        var id = created.Question?.Id ?? throw new InvalidOperationException("Question creation failed.");
        var statusChanged = await database.InTenantAsync(TenantA, transaction => content.SetStatusAsync(transaction, id, QuestionStatus.Approved));
        var approved = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, id));
        var deleted = await database.InTenantAsync(TenantA, transaction => content.DeleteAsync(transaction, id));
        var missing = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, id));

        Assert.True(statusChanged && approved?.Status == QuestionStatus.Approved && deleted && missing is null);
    }

    [SkippableFact]
    public async Task Malformed_payload_returns_a_validation_problem_without_a_new_row()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var result = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("Malformed", "[]")));

        var questions = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction, new QuestionFilter()));

        Assert.True(result.Outcome == QuestionWriteOutcome.ValidationFailed && questions.Count == 0);
    }

    [SkippableFact]
    public async Task Multiple_kind_validation_problems_are_returned_without_writing()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var result = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("Invalid options", "{\"options\":[\"\",\"\"],\"correctIndex\":4}")));

        var questions = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction, new QuestionFilter()));

        Assert.True(result.Problems.Count > 1 && result.Question is null && questions.Count == 0);
    }

    [SkippableFact]
    public async Task Oversize_rendered_question_returns_a_size_problem_without_writing()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var option = new string('a', 8_160);
        var payload = JsonSerializer.Serialize(new { options = new[] { option, option + "b", option + "c", option + "d" }, correctIndex = 0 });

        var result = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("Large", payload)));

        var questions = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction, new QuestionFilter()));

        Assert.True(result.Outcome == QuestionWriteOutcome.Oversize && questions.Count == 0);
    }

    [SkippableFact]
    public async Task Rendered_question_at_the_16_kilobyte_boundary_is_accepted()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var option = new string('a', 8_159);
        var payload = JsonSerializer.Serialize(new { options = new[] { option, "b", "c", "d" }, correctIndex = 0 });

        var result = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("Boundary", payload)));

        Assert.Equal(QuestionWriteOutcome.Success, result.Outcome);
    }

    [SkippableFact]
    public async Task Failed_edit_leaves_the_stored_question_unchanged()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("Original prompt")));
        var id = created.Question?.Id ?? throw new InvalidOperationException("Question creation failed.");

        var result = await database.InTenantAsync(TenantA, transaction => content.EditAsync(transaction, id, ValidQuestion("Changed prompt", "[]")));
        var stored = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, id));

        Assert.True(result.Outcome == QuestionWriteOutcome.ValidationFailed && stored?.Prompt == "Original prompt");
    }

    [SkippableFact]
    public async Task Older_supported_payloads_are_upgraded_before_being_stored()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources(), configuredKinds: new KindRegistry([new VersionedTestKind()]));
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var oldPayload = JsonDocument.Parse("{\"prompt\":\"old\"}").RootElement.Clone();
        var write = new QuestionWrite("versioned", 1, "trivia", "en", null, 2, "Upgraded prompt", oldPayload, null, null, QuestionStatus.Pending, QuestionSource.Admin, null);

        var result = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, write));

        Assert.True(result.Question is { KindVersion: 2 } question && question.Payload.GetProperty("upgraded").GetBoolean());
    }

    [SkippableFact]
    public async Task Concurrent_bank_questions_with_the_same_topic_have_one_duplicate_result()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var first = database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("What is France's capital?", topic: "geography/capitals")));
        var second = database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("Which ocean is largest?", topic: "geography/capitals")));

        var results = await Task.WhenAll(first, second);

        Assert.True(results.Count(result => result.Outcome == QuestionWriteOutcome.Success) == 1 && results.Count(result => result.Outcome == QuestionWriteOutcome.Duplicate) == 1);
    }

    [SkippableFact]
    public async Task Bank_prompt_fingerprint_rejects_a_reworded_duplicate_without_a_topic()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        _ = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("What is the capital of France?")));

        var duplicate = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("Which city is the capital of France?")));

        Assert.Equal(QuestionWriteOutcome.Duplicate, duplicate.Outcome);
    }

    [SkippableFact]
    public async Task Blank_topic_is_stored_as_null()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("What is the tallest mountain?", topic: "   ")));
        var id = created.Question?.Id ?? throw new InvalidOperationException("Question creation failed.");

        var stored = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, id));

        Assert.Null(stored?.Topic);
    }

    [SkippableFact]
    public async Task Blank_bank_topics_do_not_collide()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        _ = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("What is the tallest mountain?", topic: " ")));

        var result = await database.InTenantAsync(TenantA, transaction => content.CreateAsync(transaction, ValidQuestion("Which planet is closest to the Sun?", topic: string.Empty)));

        Assert.Equal(QuestionWriteOutcome.Success, result.Outcome);
    }

    [SkippableFact]
    public async Task Bank_topic_uniqueness_allows_other_languages_families_and_player_owned_questions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var player = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @player, 'Player', 'seed', 'en')", ("tenant", TenantA), ("player", player));
        await InsertQuestionAsync(owner, TenantA, Guid.NewGuid(), "choice", "trivia", "en", null, 1, "English trivia", "pending", "admin", "shared-topic");
        await InsertQuestionAsync(owner, TenantA, Guid.NewGuid(), "choice", "trivia", "nl", null, 1, "Dutch trivia", "pending", "admin", "shared-topic");
        await InsertQuestionAsync(owner, TenantA, Guid.NewGuid(), "order", "science", "en", null, 1, "English science", "pending", "admin", "shared-topic");
        await InsertQuestionAsync(owner, TenantA, Guid.NewGuid(), "choice", "trivia", "en", null, 1, "Player trivia", "pending", "player", "shared-topic", player);

        var count = await ScalarAsync<long>(owner, "select count(*) from questions where tenant_id = @tenant", ("tenant", TenantA));

        Assert.Equal(4, count);
    }

    [SkippableTheory]
    [InlineData("language")]
    [InlineData("family")]
    [InlineData("kind")]
    [InlineData("category")]
    [InlineData("level")]
    [InlineData("status")]
    [InlineData("source")]
    public async Task Filtered_listing_supports_each_envelope_filter(string field)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var ids = await SeedFilterQuestionsAsync(owner);
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var filter = field switch
        {
            "language" => new QuestionFilter(Language: "fa"),
            "family" => new QuestionFilter(Family: "science"),
            "kind" => new QuestionFilter(Kind: "order"),
            "category" => new QuestionFilter(CategoryId: CategoryB),
            "level" => new QuestionFilter(Level: 2),
            "status" => new QuestionFilter(Status: QuestionStatus.Pending),
            "source" => new QuestionFilter(Source: QuestionSource.Import),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var questions = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction, filter));

        Assert.Equal(field is "family" or "kind" ? ids.Dutch : ids.Persian, Assert.Single(questions).Id);
    }

    [SkippableTheory]
    [InlineData("GLISH", "English")]
    [InlineData("فارسی", "Persian")]
    [InlineData("vraag", "Dutch")]
    public async Task Prompt_search_finds_case_insensitive_english_persian_and_dutch_substrings(string search, string expected)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var ids = await SeedFilterQuestionsAsync(owner);
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var questions = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction, new QuestionFilter(Prompt: search)));

        var id = expected switch { "English" => ids.English, "Persian" => ids.Persian, "Dutch" => ids.Dutch, _ => throw new ArgumentOutOfRangeException(nameof(expected)) };
        Assert.Equal(id, Assert.Single(questions).Id);
    }

    [SkippableFact]
    public async Task Bucket_counts_keep_approved_and_pending_separate_for_every_bucket()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await SeedBucketsAsync(owner);
        await using var app = Quesshi.Server.Program.CreateApplication(Sources());
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var counts = await database.InTenantAsync(TenantA, transaction => content.BucketCountsAsync(transaction));

        Assert.True(counts.Count == 60 && counts.All(count => count is { Approved: 1, Pending: 1 }));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (fixture.ConnectionString is null) return;
        await using var owner = await OpenAsync("quesshi_owner");
        await ResetAsync(owner);
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Quesshi.Server.Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = await OpenAsync("quesshi_owner");
        await ResetAsync(owner);
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'questions-a', 'Questions A', 'en'), (@b, 'questions-b', 'Questions B', 'en')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@a, 'en', 'English', 'English'), (@b, 'en', 'English', 'English')", ("a", TenantA), ("b", TenantB));
        return owner;
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static QuestionWrite ValidQuestion(string prompt, string payload = "{\"options\":[\"Paris\",\"Lyon\",\"Nice\",\"Brest\"],\"correctIndex\":0}", string? topic = null) =>
        new("choice", 1, "trivia", "en", null, 2, prompt, JsonDocument.Parse(payload).RootElement.Clone(), null, topic, QuestionStatus.Pending, QuestionSource.Admin, null);

    private static async Task<(Guid English, Guid Persian, Guid Dutch)> SeedFilterQuestionsAsync(NpgsqlConnection owner)
    {
        await InsertCategoryAsync(owner, TenantA, CategoryA, "filter-english");
        await InsertCategoryAsync(owner, TenantA, CategoryB, "filter-persian");
        await InsertCategoryAsync(owner, TenantA, CategoryC, "filter-dutch");
        var english = Guid.NewGuid();
        var persian = Guid.NewGuid();
        var dutch = Guid.NewGuid();
        await InsertQuestionAsync(owner, TenantA, english, "choice", "trivia", "en", CategoryA, 1, "The ENglish lighthouse", "approved", "admin", "filter-english-topic");
        await InsertQuestionAsync(owner, TenantA, persian, "choice", "trivia", "fa", CategoryB, 2, "پرسش فارسی درباره تاریخ", "pending", "import", "filter-persian-topic");
        await InsertQuestionAsync(owner, TenantA, dutch, "order", "science", "nl", CategoryC, 3, "Nederlandse vraag over geschiedenis", "rejected", "ai", "filter-dutch-topic");
        return (english, persian, dutch);
    }

    private static async Task SeedBucketsAsync(NpgsqlConnection owner)
    {
        var categories = new[] { CategoryA, CategoryB, CategoryC };
        foreach (var (category, index) in categories.Select((value, index) => (value, index)))
            await InsertCategoryAsync(owner, TenantA, category, $"bucket-{index}");

        foreach (var language in new[] { "en", "nl" })
        foreach (var category in categories)
        foreach (var level in Enumerable.Range(1, 5))
        foreach (var kind in new[] { "choice", "order" })
        {
            var suffix = $"{language}-{category:N}-{level}-{kind}";
            await InsertQuestionAsync(owner, TenantA, Guid.NewGuid(), kind, "trivia", language, category, level, $"Approved {suffix}", "approved", "admin", null);
            await InsertQuestionAsync(owner, TenantA, Guid.NewGuid(), kind, "trivia", language, category, level, $"Pending {suffix}", "pending", "admin", null);
        }
    }

    private static Task InsertCategoryAsync(NpgsqlConnection connection, Guid tenant, Guid category, string slug) =>
        ExecuteAsync(connection, "insert into categories (tenant_id, id, slug, names) values (@tenant, @category, @slug, '{}'::jsonb)", ("tenant", tenant), ("category", category), ("slug", slug));

    private static Task InsertQuestionAsync(NpgsqlConnection connection, Guid tenant, Guid id, string kind, string family, string language, Guid? category, int level, string prompt, string status, string source, string? topic, Guid? owner = null) =>
        ExecuteAsync(connection, "insert into questions (tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, topic, fingerprint, status, source, owner_id) values (@tenant, @id, @kind, 1, @family, @language, @category, @level, @prompt, '{}'::jsonb, @topic, @fingerprint, @status, @source, @owner)",
            ("tenant", tenant), ("id", id), ("kind", kind), ("family", family), ("language", language), ("category", category), ("level", level), ("prompt", prompt), ("topic", topic), ("fingerprint", prompt.ToLowerInvariant()), ("status", status), ("source", source), ("owner", owner));

    private static Task ResetAsync(NpgsqlConnection owner) => ExecuteAsync(owner, "delete from session_participants; delete from sessions; delete from auth_sessions; delete from admin_memberships; delete from admins; delete from questions; delete from categories; delete from players; delete from tenant_languages; delete from tenants");

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = (await command.ExecuteScalarAsync())!;
        return (T)result;
    }

    private sealed class VersionedTestKind : IKindRuntime
    {
        public string Id => "versioned";
        public int Version => 2;
        public string Family => "trivia";
        public bool Gradable => false;

        public JsonElement Upgrade(int from, JsonElement payload)
        {
            if (from != 1) throw new NotSupportedException();
            return JsonSerializer.SerializeToElement(new { upgraded = payload.GetProperty("prompt").GetString() == "old" });
        }

        public JsonElement UpgradeSnapshot(int from, JsonElement snapshot) => snapshot.Clone();
        public IReadOnlyList<Problem> Validate(JsonElement payload, ValidateEnv env) => payload.GetProperty("upgraded").GetBoolean() ? [] : [new("payload.invalid", "Payload was not upgraded.")];
        public JsonElement Resolve(JsonElement payload, ResolveContext context) => JsonSerializer.SerializeToElement(new { });
        public JsonElement Card(JsonElement payload, JsonElement resolved) => payload.Clone();
        public JsonElement Reveal(JsonElement payload, JsonElement resolved) => JsonSerializer.SerializeToElement(new { });
        public JsonElement? Normalize(JsonElement payload, JsonElement resolved, JsonElement? answer) => null;
        public Grade Grade(JsonElement payload, JsonElement resolved, JsonElement? stored) => Quesshi.Shared.Grade.None;
    }
}
