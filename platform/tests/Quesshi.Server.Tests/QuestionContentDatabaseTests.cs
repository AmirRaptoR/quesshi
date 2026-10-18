using System.Globalization;
using System.Text;
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
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
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
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var createPayload = ChoicePayload(1);
        var editPayload = ChoicePayload(2);
        var created = await CreateAsync(content, database, TenantA, ValidWrite(prompt: "Which city is the capital of France?", payload: createPayload));
        var id = created.Question!.Id;
        var edited = await database.InTenantAsync(TenantA, transaction => content.EditAsync(transaction, id, ValidWrite(prompt: "Which city is the capital of France now?", payload: editPayload, status: QuestionStatus.Approved)));
        var fetched = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, id));

        Assert.Equal(QuestionWriteStatus.Success, created.Status);
        Assert.True(JsonElement.DeepEquals(createPayload, created.Question.Payload));
        Assert.Equal("trivia", edited.Question!.Family);
        Assert.Equal(1, edited.Question.KindVersion);
        Assert.Equal("Which city is the capital of France now?", fetched!.Prompt);
        Assert.Equal("choice", fetched.Kind);
        Assert.Equal("trivia", fetched.Family);
        Assert.Equal(1, fetched.KindVersion);
        Assert.True(JsonElement.DeepEquals(editPayload, fetched.Payload));
        Assert.Equal(QuestionStatus.Approved, fetched.Status);
    }

    [SkippableFact]
    public async Task Create_upgrades_an_older_supported_kind_payload_before_storing_it()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        await using var app = CreateApplication(sources, new KindRegistry([new VersionTwoChoiceKind()]));
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var original = VersionOneChoicePayload(1);
        var expected = ChoicePayload(1);

        var result = await CreateAsync(content, database, TenantA, ValidWrite(payload: original));
        var fetched = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, result.Question!.Id));

        Assert.Equal(2, result.Question!.KindVersion);
        Assert.True(JsonElement.DeepEquals(expected, result.Question.Payload));
        Assert.Equal(2, fetched!.KindVersion);
        Assert.True(JsonElement.DeepEquals(expected, fetched.Payload));
    }

    [SkippableFact]
    public async Task Edit_upgrades_an_older_supported_kind_payload_before_storing_it()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        await using var app = CreateApplication(sources, new KindRegistry([new VersionTwoChoiceKind()]));
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await CreateAsync(content, database, TenantA, ValidWrite(payload: ChoicePayload(1), kindVersion: 2));
        var expected = ChoicePayload(2);

        var result = await database.InTenantAsync(TenantA, transaction => content.EditAsync(transaction, created.Question!.Id, ValidWrite(payload: VersionOneChoicePayload(2))));
        var fetched = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, created.Question!.Id));

        Assert.Equal(2, result.Question!.KindVersion);
        Assert.True(JsonElement.DeepEquals(expected, result.Question.Payload));
        Assert.Equal(2, fetched!.KindVersion);
        Assert.True(JsonElement.DeepEquals(expected, fetched.Payload));
    }

    [SkippableFact]
    public async Task Unsupported_kind_version_does_not_create_a_question()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        await using var app = CreateApplication(sources, new KindRegistry([new VersionTwoChoiceKind()]));
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var result = await CreateAsync(content, database, TenantA, ValidWrite(kindVersion: 0));
        var questions = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction));

        Assert.Equal(QuestionWriteStatus.Invalid, result.Status);
        Assert.Contains(result.Problems!, problem => problem.Code == "payload.malformed");
        Assert.Empty(questions);
    }

    [SkippableFact]
    public async Task Unsupported_kind_version_does_not_change_a_question()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        await using var app = CreateApplication(sources, new KindRegistry([new VersionTwoChoiceKind()]));
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await CreateAsync(content, database, TenantA, ValidWrite(payload: ChoicePayload(1), kindVersion: 2));

        var result = await database.InTenantAsync(TenantA, transaction => content.EditAsync(transaction, created.Question!.Id, ValidWrite(prompt: "A changed prompt", payload: ChoicePayload(2), kindVersion: 3)));
        var fetched = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, created.Question!.Id));

        Assert.Equal(QuestionWriteStatus.Invalid, result.Status);
        Assert.Contains(result.Problems!, problem => problem.Code == "payload.malformed");
        Assert.Equal(created.Question!.Prompt, fetched!.Prompt);
        Assert.True(JsonElement.DeepEquals(created.Question.Payload, fetched.Payload));
    }

    [SkippableFact]
    public async Task Question_status_can_change_without_rewriting_the_envelope()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
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
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
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
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var created = await CreateAsync(content, database, TenantA, ValidWrite());
        var malformed = await CreateAsync(content, database, TenantA, ValidWrite(payload: JsonSerializer.SerializeToElement(Array.Empty<string>())));
        var invalid = await database.InTenantAsync(TenantA, transaction => content.EditAsync(transaction, created.Question!.Id, ValidWrite(payload: JsonSerializer.SerializeToElement(new { options = new[] { "", "", "one" }, correctIndex = 9 }))));
        var unchanged = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, created.Question!.Id));
        var stored = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction));

        Assert.Equal(QuestionWriteStatus.Invalid, malformed.Status);
        Assert.Equal(["options.tooFew", "options.blank", "correctIndex.outOfRange"], invalid.Problems!.Select(problem => problem.Code));
        Assert.Equal("What is the capital of France?", unchanged!.Prompt);
        Assert.Single(stored);
    }

    [SkippableFact]
    public async Task Content_service_accepts_the_16_kb_boundary_and_rejects_a_larger_snapshot()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var atLimit = new SnapshotSizedChoiceKind(16_384);
        var overLimit = new SnapshotSizedChoiceKind(16_385);
        await using var sources = Sources();
        await using var app = CreateApplication(sources, new KindRegistry([atLimit, overLimit]));
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var acceptable = await CreateAsync(content, database, TenantA, ValidWrite(kind: atLimit.Id, prompt: "اندازهٔ کارت"));
        var rejected = await CreateAsync(content, database, TenantA, ValidWrite(kind: overLimit.Id, prompt: "Which very long option is correct?"));
        var rejectedEdit = await database.InTenantAsync(TenantA, transaction => content.EditAsync(transaction, acceptable.Question!.Id, ValidWrite(kind: overLimit.Id, prompt: "This edit must remain unchanged.")));
        var unchanged = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, acceptable.Question!.Id));

        Assert.Equal(QuestionWriteStatus.Success, acceptable.Status);
        Assert.Equal(QuestionWriteStatus.Oversize, rejected.Status);
        Assert.Contains(rejected.Problems!, problem => problem.Code == "content.tooLarge");
        Assert.Equal(QuestionWriteStatus.Oversize, rejectedEdit.Status);
        Assert.Equal("اندازهٔ کارت", unchanged!.Prompt);
        Assert.Single(await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction)));
    }

    [SkippableFact]
    public async Task Bank_topic_collisions_are_serialized_while_player_owned_questions_are_exempt()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var player = await InsertPlayerAsync(owner, TenantA);
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var bank = ValidWrite(prompt: "Who painted Guernica?", topic: "france|capital");
        var competitor = bank with { Prompt = "Which planet has the largest rings?" };
        var results = await Task.WhenAll(CreateAsync(content, database, TenantA, bank), CreateAsync(content, database, TenantA, competitor));
        var storedBank = await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction));
        var playerWrite = bank with { Source = QuestionSource.Player, OwnerId = player, Prompt = "Which river crosses Paris for a player?" };

        Assert.Equal(1, results.Count(result => result.Status == QuestionWriteStatus.Success));
        Assert.Equal(1, results.Count(result => result.Status == QuestionWriteStatus.Duplicate));
        Assert.Single(storedBank);
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
    public async Task The_topic_index_rejects_distinct_bank_prompts_and_allows_null_topics()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "topic-index");

        _ = await InsertRawQuestionAsync(owner, TenantA, category, 1, "en", "choice", "Who painted Guernica?", topic: "art|painting");
        var collision = await Assert.ThrowsAsync<PostgresException>(() => InsertRawQuestionAsync(owner, TenantA, category, 1, "en", "choice", "Which planet has the largest rings?", topic: "art|painting"));
        var firstWithoutTopic = await InsertRawQuestionAsync(owner, TenantA, category, 1, "en", "choice", "A question without a topic");
        var secondWithoutTopic = await InsertRawQuestionAsync(owner, TenantA, category, 1, "en", "choice", "Another question without a topic");

        Assert.Equal(PostgresErrorCodes.UniqueViolation, collision.SqlState);
        Assert.NotEqual(firstWithoutTopic, secondWithoutTopic);
    }

    [SkippableFact]
    public async Task A_duplicate_result_does_not_abort_the_callers_transaction()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var outcomes = await database.InTenantAsync(TenantA, async transaction =>
        {
            var first = await content.CreateAsync(transaction, ValidWrite(prompt: "Who painted Guernica?", topic: "art|painting"));
            var duplicate = await content.CreateAsync(transaction, ValidWrite(prompt: "Which planet has the largest rings?", topic: "art|painting"));
            var afterDuplicate = await content.CreateAsync(transaction, ValidWrite(prompt: "What is the deepest ocean?", topic: "ocean|depth"));
            return (first, duplicate, afterDuplicate, await content.ListAsync(transaction));
        });

        Assert.Equal(QuestionWriteStatus.Success, outcomes.first.Status);
        Assert.Equal(QuestionWriteStatus.Duplicate, outcomes.duplicate.Status);
        Assert.Equal(QuestionWriteStatus.Success, outcomes.afterDuplicate.Status);
        Assert.Equal(2, outcomes.Item4.Count);
    }

    [SkippableFact]
    public async Task Prompt_fingerprints_are_compared_within_the_same_language_and_category()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var firstCategory = await InsertCategoryAsync(owner, TenantA, "fingerprints-one");
        var secondCategory = await InsertCategoryAsync(owner, TenantA, "fingerprints-two");
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var write = ValidWrite(prompt: "Who painted Guernica?", category: firstCategory);

        var first = await CreateAsync(content, database, TenantA, write);
        var duplicate = await CreateAsync(content, database, TenantA, write with { Prompt = "Who painted the Guernica?" });
        var otherCategory = await CreateAsync(content, database, TenantA, write with { CategoryId = secondCategory });
        var stored = await database.InTenantAsync(TenantA, transaction => content.GetAsync(transaction, otherCategory.Question!.Id));

        Assert.Equal(QuestionWriteStatus.Success, first.Status);
        Assert.Equal(QuestionWriteStatus.Duplicate, duplicate.Status);
        Assert.Equal(QuestionWriteStatus.Success, otherCategory.Status);
        Assert.Equal(PromptFingerprint.Normalise(write.Prompt), stored!.Fingerprint);
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
        var persianPending = await InsertRawQuestionAsync(owner, TenantA, categories[0], 1, "fa", "choice", "پایتخت فرانسه کدام شهر است؟", "pending", "admin");
        var persianApproved = await InsertRawQuestionAsync(owner, TenantA, categories[0], 1, "fa", "choice", "فرانسه در کدام قاره است؟", "approved", "admin");
        var englishApproved = await InsertRawQuestionAsync(owner, TenantA, categories[0], 2, "en", "choice", "The Capital of France is Paris.", "approved", "import");
        var dutchPending = await InsertRawQuestionAsync(owner, TenantA, categories[1], 3, "nl", "order", "De échte hoofdstad van België is Brussel.", "pending", "ai", "poll");
        var draftLiteralPercent = await InsertRawQuestionAsync(owner, TenantA, categories[1], 4, "en", "choice", "This is 100% certain.", "draft", "admin");
        var rejectedLiteralUnderscore = await InsertRawQuestionAsync(owner, TenantA, categories[2], 5, "nl", "order", "Dit_onderwerp is afgewezen.", "rejected", "import", "poll");
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        var byLanguage = await ListIdsAsync(content, database, new(Language: "fa"));
        var byFamily = await ListIdsAsync(content, database, new(Family: "poll"));
        var byKind = await ListIdsAsync(content, database, new(Kind: "order"));
        var byCategory = await ListIdsAsync(content, database, new(CategoryId: categories[1]));
        var byLevel = await ListIdsAsync(content, database, new(Level: 4));
        var byStatus = await ListIdsAsync(content, database, new(Status: QuestionStatus.Pending));
        var bySource = await ListIdsAsync(content, database, new(Source: QuestionSource.Import));
        var persian = await ListIdsAsync(content, database, new(Prompt: "فرانسه"));
        var english = await ListIdsAsync(content, database, new(Prompt: "cApItAl"));
        var dutch = await ListIdsAsync(content, database, new(Prompt: "ÉCHTE"));
        var literalPercent = await ListIdsAsync(content, database, new(Prompt: "100%"));
        var literalUnderscore = await ListIdsAsync(content, database, new(Prompt: "_"));
        var buckets = await database.InTenantAsync(TenantA, transaction => content.BucketCountsAsync(transaction));

        Assert.Equivalent(new[] { persianPending, persianApproved }, byLanguage);
        Assert.Equivalent(new[] { dutchPending, rejectedLiteralUnderscore }, byFamily);
        Assert.Equivalent(new[] { dutchPending, rejectedLiteralUnderscore }, byKind);
        Assert.Equivalent(new[] { dutchPending, draftLiteralPercent }, byCategory);
        Assert.Equivalent(new[] { draftLiteralPercent }, byLevel);
        Assert.Equivalent(new[] { persianPending, dutchPending }, byStatus);
        Assert.Equivalent(new[] { englishApproved, rejectedLiteralUnderscore }, bySource);
        Assert.Equivalent(new[] { persianPending, persianApproved }, persian);
        Assert.Equivalent(new[] { englishApproved }, english);
        Assert.Equivalent(new[] { dutchPending }, dutch);
        Assert.Equivalent(new[] { draftLiteralPercent }, literalPercent);
        Assert.Equivalent(new[] { rejectedLiteralUnderscore }, literalUnderscore);
        Assert.Equivalent(
            new[]
            {
                new QuestionBucketCount("fa", categories[0], 1, "choice", 1, 1),
                new QuestionBucketCount("en", categories[0], 2, "choice", 1, 0),
                new QuestionBucketCount("nl", categories[1], 3, "order", 0, 1),
                new QuestionBucketCount("en", categories[1], 4, "choice", 0, 0),
                new QuestionBucketCount("nl", categories[2], 5, "order", 0, 0),
            },
            buckets);
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
    private static WebApplication CreateApplication(DatabaseSources sources, IKindRegistry? kinds = null) => Quesshi.Server.Program.CreateApplication(sources, 11298, 30298, configuredKinds: kinds);
    private static Task ResetDatabaseAsync(NpgsqlConnection owner) => ExecuteAsync(owner, "delete from session_answers; delete from session_items; delete from session_participants; delete from sessions; delete from questions; delete from categories; delete from players; delete from tenant_languages; delete from tenants");
    private async Task<NpgsqlConnection> OpenAsync(string role) { var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role)); await connection.OpenAsync(); return connection; }
    private static QuestionWrite ValidWrite(string prompt = "What is the capital of France?", JsonElement? payload = null, QuestionStatus status = QuestionStatus.Pending, string? topic = null, string kind = "choice", Guid? category = null, int kindVersion = 1) => new(kind, kindVersion, "trivia", "en", category, 1, prompt, payload ?? ChoicePayload(1), null, topic, status, QuestionSource.Admin, null);
    private static JsonElement ChoicePayload(int firstLength) => JsonSerializer.SerializeToElement(new { options = new[] { new string('a', firstLength), "bravo", "charlie", "delta" }, correctIndex = 0 });
    private static JsonElement VersionOneChoicePayload(int firstLength) => JsonSerializer.SerializeToElement(new { choices = new[] { new string('a', firstLength), "bravo", "charlie", "delta" }, correctIndex = 0 });
    private static Task<QuestionWriteResult> CreateAsync(IContent content, ITenantDb database, Guid tenant, QuestionWrite write) => database.InTenantAsync(tenant, transaction => content.CreateAsync(transaction, write));
    private static async Task<IReadOnlyList<Guid>> ListIdsAsync(IContent content, ITenantDb database, QuestionFilter filters) => (await database.InTenantAsync(TenantA, transaction => content.ListAsync(transaction, filters))).Select(question => question.Id).ToArray();
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
            if (from == 1)
            {
                if (!payload.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                    || !payload.TryGetProperty("correctIndex", out var correctIndex) || !correctIndex.TryGetInt32(out var index))
                    throw new JsonException("Choice v1 payload is invalid.");
                return new ChoicePayload(choices.EnumerateArray().Select(option => option.GetString() ?? throw new JsonException("Choice options are invalid.")).ToArray(), index);
            }
            if (from != 2) throw new NotSupportedException($"choice v{from} cannot be upgraded by choice v2.");
            return JsonSerializer.Deserialize(payload, ChoiceJsonContext.Default.ChoicePayload)
                ?? throw new JsonException("Choice payload is invalid.");
        }
    }

    private sealed class SnapshotSizedChoiceKind : ChoiceKind
    {
        private readonly string _cardText;
        private readonly string _revealText;

        public SnapshotSizedChoiceKind(int snapshotBytes)
        {
            Id = $"sized-choice-{snapshotBytes}";
            (_cardText, _revealText) = TextsForSnapshotBytes(snapshotBytes);
        }

        public override string Id { get; }

        public override ChoiceCard Card(Item<ChoicePayload, ChoiceResolved> item) => new([_cardText]);

        public override ChoiceReveal Reveal(Item<ChoicePayload, ChoiceResolved> item) => new(0, _revealText);

        private static (string Card, string Reveal) TextsForSnapshotBytes(int target)
        {
            for (var cardLength = 1; cardLength <= target; cardLength++)
            {
                var card = new string('é', cardLength);
                var revealLength = target - SnapshotBytes(card, string.Empty);
                if (revealLength < 0) break;
                var reveal = new string('a', revealLength);
                if (SnapshotBytes(card, reveal) == target) return (card, reveal);
            }

            throw new InvalidOperationException($"No Choice snapshot has exactly {target} bytes.");
        }

        private static int SnapshotBytes(string card, string reveal)
        {
            var runtime = (IKindRuntime)new SnapshotSizedChoiceKindProbe(card, reveal);
            var payload = ChoicePayload(1);
            var resolved = runtime.Resolve(payload, new ResolveContext("size", 0, []));
            return Encoding.UTF8.GetByteCount(runtime.Card(payload, resolved).GetRawText()) + Encoding.UTF8.GetByteCount(runtime.Reveal(payload, resolved).GetRawText());
        }

        private sealed class SnapshotSizedChoiceKindProbe : ChoiceKind
        {
            public SnapshotSizedChoiceKindProbe(string cardText, string revealText) => (CardText, RevealText) = (cardText, revealText);

            public override ChoiceCard Card(Item<ChoicePayload, ChoiceResolved> item) => new([CardText]);
            public override ChoiceReveal Reveal(Item<ChoicePayload, ChoiceResolved> item) => new(0, RevealText);
            public string CardText { get; }
            public string RevealText { get; }
        }
    }
}
