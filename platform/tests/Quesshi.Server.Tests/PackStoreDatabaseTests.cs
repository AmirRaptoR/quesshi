using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Application;
using Quesshi.Application.Packs;
using Quesshi.Features.Sources.Bank;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Plugin.Api;
using Quesshi.Server.Accounts;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class PackStoreDatabaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Migration_creates_packs_and_pack_items_with_the_schema_constraints()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seed = await SeedAsync();
        await using var owner = await OpenAsync("quesshi_owner");

        foreach (var table in new[] { "packs", "pack_items" })
        {
            Assert.Equal("owner:core", await ScalarAsync<string>(owner, "select obj_description(@table::regclass)", ("table", table)));
            Assert.True(await ScalarAsync<bool>(owner, "select relrowsecurity from pg_class where oid = @table::regclass", ("table", table)));
        }
        Assert.Equal(
            ["created_at", "description", "id", "lang", "owner_id", "tenant_id", "title", "updated_at", "visibility"],
            await ColumnsAsync(owner, "packs"));
        Assert.Equal(["pack_id", "position", "question_id", "tenant_id"], await ColumnsAsync(owner, "pack_items"));
        Assert.Contains("private", await ScalarAsync<string>(owner, "select column_default from information_schema.columns where table_name = 'packs' and column_name = 'visibility'"));
        Assert.True(await ScalarAsync<bool>(owner, "select condeferrable and condeferred from pg_constraint where conrelid = 'pack_items'::regclass and contype = 'u'"));
        Assert.True(await ScalarAsync<bool>(owner, "select exists (select 1 from pg_indexes where indexname = 'packs_owner')"));
        Assert.True(await ScalarAsync<bool>(owner, "select exists (select 1 from pg_indexes where indexname = 'pack_items_question')"));

        var bad = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner,
            "insert into packs (tenant_id, id, title, lang, visibility) values (@tenant, @id, 't', 'en', 'public')", ("tenant", seed.Tenant), ("id", Guid.NewGuid())));
        Assert.Equal("23514", bad.SqlState);
        var pack = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into packs (tenant_id, id, title, lang) values (@tenant, @id, 't', 'en')", ("tenant", seed.Tenant), ("id", pack));
        var question = await InsertQuestionAsync(owner, seed.Tenant, null, "bank-dup-check");
        await ExecuteAsync(owner, "insert into pack_items (tenant_id, pack_id, question_id, position) values (@tenant, @pack, @question, 0)", ("tenant", seed.Tenant), ("pack", pack), ("question", question));
        var second = await InsertQuestionAsync(owner, seed.Tenant, null, "bank-dup-check-2");
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner,
            "insert into pack_items (tenant_id, pack_id, question_id, position) values (@tenant, @pack, @question, 0)", ("tenant", seed.Tenant), ("pack", pack), ("question", second)));
        Assert.Equal("23505", duplicate.SqlState);
    }

    [SkippableFact]
    public async Task Pack_store_creates_renames_describes_and_deletes_a_pack()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seed = await SeedAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var packs = new PostgresPacks(new TenantLocks());

        var created = await database.InTenantAsync(seed.Tenant, tx => packs.CreateAsync(tx, seed.Player, "  Capitals ", null, "en"));
        Assert.Equal("Capitals", created.Title);
        Assert.Null(created.Description);
        Assert.Equal(PackVisibility.Private, created.Visibility);
        Assert.Equal(seed.Player, created.OwnerId);

        Assert.True(await database.InTenantAsync(seed.Tenant, tx => packs.RenameAsync(tx, created.Id, "World capitals")));
        Assert.True(await database.InTenantAsync(seed.Tenant, tx => packs.DescribeAsync(tx, created.Id, "Hard ones")));
        var edited = await database.InTenantAsync(seed.Tenant, tx => packs.GetAsync(tx, created.Id));
        Assert.Equal("World capitals", edited!.Title);
        Assert.Equal("Hard ones", edited.Description);
        Assert.Single(await database.InTenantAsync(seed.Tenant, tx => packs.ListOwnedAsync(tx, seed.Player)));

        Assert.True(await database.InTenantAsync(seed.Tenant, tx => packs.DeleteAsync(tx, created.Id)));
        Assert.Null(await database.InTenantAsync(seed.Tenant, tx => packs.GetAsync(tx, created.Id)));
        Assert.False(await database.InTenantAsync(seed.Tenant, tx => packs.DeleteAsync(tx, created.Id)));
        Assert.Empty(await database.InTenantAsync(seed.Tenant, tx => packs.ListOwnedAsync(tx, seed.Player)));
    }

    [SkippableFact]
    public async Task Pack_store_sets_each_visibility_and_rejects_any_other_value()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seed = await SeedAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var packs = new PostgresPacks(new TenantLocks());
        var pack = await database.InTenantAsync(seed.Tenant, tx => packs.CreateAsync(tx, seed.Player, "Visible", null, "en"));

        foreach (var visibility in new[] { PackVisibility.Unlisted, PackVisibility.Tenant, PackVisibility.Private })
        {
            Assert.True(await database.InTenantAsync(seed.Tenant, tx => packs.SetVisibilityAsync(tx, pack.Id, visibility)));
            Assert.Equal(visibility, (await database.InTenantAsync(seed.Tenant, tx => packs.GetAsync(tx, pack.Id)))!.Visibility);
        }

        await database.InTenantAsync(seed.Tenant, tx => packs.SetVisibilityAsync(tx, pack.Id, PackVisibility.Tenant));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => database.InTenantAsync(seed.Tenant, tx => packs.SetVisibilityAsync(tx, pack.Id, (PackVisibility)99)));
        Assert.Equal(PackVisibility.Tenant, (await database.InTenantAsync(seed.Tenant, tx => packs.GetAsync(tx, pack.Id)))!.Visibility);
        Assert.False(PackVisibilities.TryParse("public", out _));
        Assert.True(PackVisibilities.TryParse("unlisted", out var parsed));
        Assert.Equal(PackVisibility.Unlisted, parsed);
    }

    [SkippableFact]
    public async Task Player_written_question_is_stored_as_an_approved_player_question()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seed = await SeedAsync();
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var result = await database.InTenantAsync(seed.Tenant, tx => content.CreateAsync(tx, PlayerWrite("Mine?", "shared-topic", seed.Player)));

        Assert.Equal(QuestionWriteStatus.Success, result.Status);
        Assert.Equal(QuestionSource.Player, result.Question!.Source);
        Assert.Equal(seed.Player, result.Question.OwnerId);
        Assert.Equal(QuestionStatus.Approved, result.Question.Status);
        await using var owner = await OpenAsync("quesshi_owner");
        Assert.Equal("player|approved", await ScalarAsync<string>(owner, "select source || '|' || status from questions where id = @id", ("id", result.Question.Id)));
    }

    [SkippableFact]
    public async Task Player_written_questions_may_share_a_topic_with_each_other_and_the_bank()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seed = await SeedAsync();
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();
        await using var owner = await OpenAsync("quesshi_owner");
        await InsertQuestionAsync(owner, seed.Tenant, null, "Bank prompt", topic: "france|capital");

        var first = await database.InTenantAsync(seed.Tenant, tx => content.CreateAsync(tx, PlayerWrite("Player one?", "france|capital", seed.Player)));
        var second = await database.InTenantAsync(seed.Tenant, tx => content.CreateAsync(tx, PlayerWrite("Player two?", "france|capital", seed.Player)));

        Assert.Equal(QuestionWriteStatus.Success, first.Status);
        Assert.Equal(QuestionWriteStatus.Success, second.Status);
        Assert.Equal(3L, await ScalarAsync<long>(owner, "select count(*) from questions where tenant_id = @tenant and topic = 'france|capital'", ("tenant", seed.Tenant)));
    }

    [SkippableFact]
    public async Task Bank_draw_never_returns_a_player_written_question()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seed = await SeedAsync();
        await using var owner = await OpenAsync("quesshi_owner");
        var category = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into categories (tenant_id, id, slug, names) values (@tenant, @id, 'draw', '{\"en\":\"Draw\"}'::jsonb)", ("tenant", seed.Tenant), ("id", category));
        await ExecuteAsync(owner, "insert into tenant_capabilities (tenant_id, type, key, entitled, enabled, settings) values (@tenant, 'source', 'bank', true, true, @settings::jsonb)",
            ("tenant", seed.Tenant), ("settings", JsonSerializer.Serialize(new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [category] }))));
        var bankQuestion = await InsertQuestionAsync(owner, seed.Tenant, null, "Bank only", category: category);
        for (var number = 0; number < 5; number++) await InsertQuestionAsync(owner, seed.Tenant, seed.Player, $"Player {number}", category: category);
        await using var sources = Sources();
        var database = new TenantDb(sources);

        Task<Picked> Draw(int count) => database.InTenantAsync(seed.Tenant, tx => new BankSource().PickAsync(tx,
            JsonSerializer.SerializeToElement(new BankParams()), new SourceDrawPlan(count, "en", [new SourceKind("choice", "trivia")], [1], 3), Guid.Empty));

        Assert.Equal([bankQuestion], Assert.IsType<PickedQuestionIds>(await Draw(1)).Ids);
        await Assert.ThrowsAsync<NotEnoughQuestionsException>(() => Draw(2));
    }

    [SkippableFact]
    public async Task Admin_question_list_excludes_player_written_questions_unless_asked()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seed = await SeedAsync();
        await using var owner = await OpenAsync("quesshi_owner");
        var bank = await InsertQuestionAsync(owner, seed.Tenant, null, "Bank question");
        var player = await InsertQuestionAsync(owner, seed.Tenant, seed.Player, "Player question");
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var byDefault = await database.InTenantAsync(seed.Tenant, tx => content.ListAsync(tx));
        var filtered = await database.InTenantAsync(seed.Tenant, tx => content.ListAsync(tx, new QuestionFilter(Status: QuestionStatus.Approved)));
        var buckets = await database.InTenantAsync(seed.Tenant, tx => content.BucketCountsAsync(tx));
        var included = await database.InTenantAsync(seed.Tenant, tx => content.ListAsync(tx, new QuestionFilter(IncludePlayerWritten: true)));

        Assert.Equal([bank], byDefault.Select(question => question.Id));
        Assert.Equal([bank], filtered.Select(question => question.Id));
        Assert.Equal(1, buckets.Sum(bucket => bucket.Approved));
        Assert.Equivalent(new[] { bank, player }, included.Select(question => question.Id));
    }

    [SkippableFact]
    public async Task Reordering_fifty_items_leaves_positions_zero_to_forty_nine_without_gaps_or_duplicates()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seed = await SeedAsync();
        await using var owner = await OpenAsync("quesshi_owner");
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var packs = new PostgresPacks(new TenantLocks());
        var pack = await database.InTenantAsync(seed.Tenant, tx => packs.CreateAsync(tx, seed.Player, "Fifty", null, "en"));
        var questions = new List<Guid>();
        for (var number = 0; number < 50; number++)
        {
            var id = await InsertQuestionAsync(owner, seed.Tenant, seed.Player, $"Q{number}");
            questions.Add(id);
            Assert.True(await database.InTenantAsync(seed.Tenant, tx => packs.AddQuestionAsync(tx, pack.Id, id)));
        }
        Assert.False(await database.InTenantAsync(seed.Tenant, tx => packs.AddQuestionAsync(tx, pack.Id, questions[0])));

        var shuffled = questions.OrderBy(id => id).Reverse().ToArray();
        Assert.True(await database.InTenantAsync(seed.Tenant, tx => packs.ReorderAsync(tx, pack.Id, shuffled)));

        var items = await database.InTenantAsync(seed.Tenant, tx => packs.ListQuestionsAsync(tx, pack.Id));
        Assert.Equal(Enumerable.Range(0, 50), items.Select(item => item.Position));
        Assert.Equal(shuffled, items.Select(item => item.QuestionId));
        Assert.False(await database.InTenantAsync(seed.Tenant, tx => packs.ReorderAsync(tx, pack.Id, shuffled.Skip(1).ToArray())));

        Assert.True(await database.InTenantAsync(seed.Tenant, tx => packs.RemoveQuestionAsync(tx, pack.Id, shuffled[10])));
        var afterRemoval = await database.InTenantAsync(seed.Tenant, tx => packs.ListQuestionsAsync(tx, pack.Id));
        Assert.Equal(Enumerable.Range(0, 49), afterRemoval.Select(item => item.Position));
        Assert.DoesNotContain(shuffled[10], afterRemoval.Select(item => item.QuestionId));
    }

    [SkippableFact]
    public async Task Player_cleanup_removes_their_packs_and_questions_but_played_sessions_keep_their_snapshot()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seed = await SeedAsync();
        await using var owner = await OpenAsync("quesshi_owner");
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var packs = new PostgresPacks(new TenantLocks());
        var other = await InsertPlayerAsync(owner, seed.Tenant);
        var played = await InsertQuestionAsync(owner, seed.Tenant, seed.Player, "Played from a pack");
        var kept = await InsertQuestionAsync(owner, seed.Tenant, other, "Someone else's");
        var pack = await database.InTenantAsync(seed.Tenant, tx => packs.CreateAsync(tx, seed.Player, "Doomed", null, "en"));
        var otherPack = await database.InTenantAsync(seed.Tenant, tx => packs.CreateAsync(tx, other, "Kept", null, "en"));
        await database.InTenantAsync(seed.Tenant, tx => packs.AddQuestionAsync(tx, pack.Id, played));
        await database.InTenantAsync(seed.Tenant, tx => packs.AddQuestionAsync(tx, otherPack.Id, kept));
        var session = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, phase, started_at, ended_at) values (@tenant, @session, @code, 'test', @player, 'en', 'finished', 1, '{}'::jsonb, 'finished', now(), now())",
            ("tenant", seed.Tenant), ("session", session), ("code", session.ToString("N")[..8]), ("player", seed.Player));
        await ExecuteAsync(owner, "insert into session_items (tenant_id, session_id, slot, question_id, kind, kind_version, prompt, payload, card, reveal) values (@tenant, @session, 0, @question, 'choice', 1, 'Played from a pack', '{}'::jsonb, '{}'::jsonb, '{}'::jsonb)",
            ("tenant", seed.Tenant), ("session", session), ("question", played));
        var registry = FeatureRegistry.Create([]);
        var cleanup = new CorePlayerCleanup(new TenantLocks(), new DurableEvents(registry, new TenantJobs(), new TenantLocks(), new NullSessionEffectsNotifier()));

        await database.InTenantAsync(seed.Tenant, async tx => { await cleanup.CleanAsync(tx, seed.Player, CancellationToken.None); return 0; });

        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from packs where owner_id = @player", ("player", seed.Player)));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from questions where owner_id = @player", ("player", seed.Player)));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from pack_items where pack_id = @pack", ("pack", pack.Id)));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from packs where id = @pack", ("pack", otherPack.Id)));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from pack_items where pack_id = @pack", ("pack", otherPack.Id)));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from questions where id = @id", ("id", kept)));
        Assert.Equal("Played from a pack", await ScalarAsync<string>(owner, "select prompt from session_items where session_id = @session and slot = 0", ("session", session)));
        Assert.True(await ScalarAsync<bool>(owner, "select question_id is null from session_items where session_id = @session and slot = 0", ("session", session)));
    }

    [SkippableFact]
    public async Task Tenant_isolation_catalogue_passes_for_packs_and_pack_items()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await SeedAsync();
        await using var owner = await OpenAsync("quesshi_owner");
        await TenantIsolationTestHelper.AssertTenantCatalogueAsync(owner, GlobalTables);
        foreach (var table in new[] { "packs", "pack_items" })
            Assert.True(await ScalarAsync<bool>(owner, "select exists (select 1 from pg_policies where tablename = @table)", ("table", table)));
    }

    private static readonly IReadOnlySet<string> GlobalTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "tenants", "tenant_hosts", "tenant_languages", "tenant_capabilities", "accounts", "auth_sessions", "jobs"
    };

    private record Seed(Guid Tenant, Guid Player);

    private async Task<Seed> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = await OpenAsync("quesshi_owner");
        var tenant = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@id, @slug, 'Packs', 'en')", ("id", tenant), ("slug", $"packs-{tenant:N}"));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@id, 'en', 'English', 'English')", ("id", tenant));
        return new(tenant, await InsertPlayerAsync(owner, tenant));
    }

    private static async Task<Guid> InsertPlayerAsync(NpgsqlConnection owner, Guid tenant)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @id, 'Player', 'seed', 'en')", ("tenant", tenant), ("id", id));
        return id;
    }

    private static async Task<Guid> InsertQuestionAsync(NpgsqlConnection owner, Guid tenant, Guid? ownerId, string prompt, Guid? category = null, string? topic = null)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into questions (tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, topic, fingerprint, status, source, owner_id) values (@tenant, @id, 'choice', 1, 'trivia', 'en', @category, 1, @prompt, '{\"options\":[\"a\",\"b\",\"c\",\"d\"],\"correctIndex\":0}'::jsonb, @topic, @prompt, 'approved', @source, @owner)",
            ("tenant", tenant), ("id", id), ("category", (object?)category ?? DBNull.Value), ("prompt", prompt), ("topic", (object?)topic ?? DBNull.Value),
            ("source", ownerId is null ? "admin" : "player"), ("owner", (object?)ownerId ?? DBNull.Value));
        return id;
    }

    private static QuestionWrite PlayerWrite(string prompt, string? topic, Guid ownerId) => new("choice", 1, "trivia", "en", null, 1, prompt,
        JsonSerializer.SerializeToElement(new { options = new[] { "a", "bravo", "charlie", "delta" }, correctIndex = 0 }), null, topic, QuestionStatus.Approved, QuestionSource.Player, ownerId);

    private static async Task<string[]> ColumnsAsync(NpgsqlConnection owner, string table)
    {
        await using var command = new NpgsqlCommand("select column_name from information_schema.columns where table_name = @table order by column_name", owner);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns.ToArray();
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
    private static WebApplication CreateApplication(DatabaseSources sources) => Program.CreateApplication(sources, TestPorts.Allocate(), TestPorts.Allocate());
    private async Task<NpgsqlConnection> OpenAsync(string role) { var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role)); await connection.OpenAsync(); return connection; }
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) { await using var command = new NpgsqlCommand(sql, connection); foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value); await command.ExecuteNonQueryAsync(); }
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) { await using var command = new NpgsqlCommand(sql, connection); foreach (var (name, parameterValue) in parameters) command.Parameters.AddWithValue(name, parameterValue); var result = (await command.ExecuteScalarAsync())!; return result is T typed ? typed : (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture); }
}
