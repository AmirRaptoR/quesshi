using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Server;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class SessionDatabaseTests(PostgresFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("390d78fd-a9a1-40db-ad0b-f7b62e24b0da");
    private static readonly Guid TenantB = Guid.Parse("202dfd04-fb1e-41af-8997-b0d604032d91");
    private static readonly Guid OwnerA = Guid.Parse("7004ef53-3e97-4f94-bbbf-ccfa0e0695a1");
    private static readonly Guid OwnerB = Guid.Parse("643ddf3b-44c3-4a59-8b08-a8f1711a0fd7");

    [Fact]
    public void Core_migrations_include_the_session_storage_contract_after_jobs()
    {
        var names = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly).Select(migration => migration.Name).ToArray();

        Assert.Contains("20261013T0102_core_sessions.sql", names);
        Assert.Contains("20261014T0204_core_deadlines.sql", names);
        Assert.True(Array.IndexOf(names, "20261013T0101_core_jobs.sql") < Array.IndexOf(names, "20261013T0102_core_sessions.sql"));
    }

    [SkippableFact]
    public async Task Due_sessions_returns_only_due_unheld_rows_to_the_system_sweep()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var retrying = Guid.NewGuid();
        var future = Guid.NewGuid();
        var asOf = DateTimeOffset.UtcNow;

        await InsertSessionAsync(owner, TenantA, first, OwnerA, "due-first");
        await InsertSessionAsync(owner, TenantB, second, OwnerB, "due-second");
        await InsertSessionAsync(owner, TenantA, retrying, OwnerA, "retrying");
        await InsertSessionAsync(owner, TenantA, future, OwnerA, "future");
        await ExecuteAsync(owner, "update sessions set due_at = @due where id = @session", ("due", asOf.AddSeconds(-3)), ("session", first));
        await ExecuteAsync(owner, "update sessions set due_at = @due where id = @session", ("due", asOf.AddSeconds(-2)), ("session", second));
        await ExecuteAsync(owner, "update sessions set due_at = @due, retry_at = @retry where id = @session", ("due", asOf.AddSeconds(-4)), ("retry", asOf.AddMinutes(1)), ("session", retrying));
        await ExecuteAsync(owner, "update sessions set due_at = @due where id = @session", ("due", asOf.AddMinutes(1)), ("session", future));

        await using var system = await OpenAsync("quesshi_system");
        await using var command = new NpgsqlCommand("select id from due_sessions(@asOf, @maxRows, @skip)", system);
        command.Parameters.AddWithValue("asOf", asOf);
        command.Parameters.AddWithValue("maxRows", 256);
        command.Parameters.AddWithValue("skip", new[] { first });
        await using var reader = await command.ExecuteReaderAsync();
        var actual = new List<Guid>();
        while (await reader.ReadAsync()) actual.Add(reader.GetGuid(0));
        Assert.Equal([second], actual);

        Assert.False(await ScalarAsync<bool>(owner, "select has_function_privilege('quesshi_app', 'due_sessions(timestamptz, int, uuid[])', 'EXECUTE')"));
        Assert.True(await ScalarAsync<bool>(owner, "select has_function_privilege('quesshi_system', 'due_sessions(timestamptz, int, uuid[])', 'EXECUTE')"));
    }

    [SkippableFact]
    public async Task Session_migration_creates_the_catalogue_and_questions_adds_content_foreign_keys()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();

        foreach (var table in new[] { "accounts", "players", "sessions", "session_participants", "session_items", "session_answers" })
        {
            Assert.True(await ScalarAsync<bool>(owner, "select to_regclass(@table) is not null", ("table", table)));
        }

        foreach (var index in new[]
        {
            "players_account", "sessions_code", "sessions_due", "participants_history", "session_items_question",
            "session_items_media", "session_items_category", "session_answers_player"
        })
        {
            Assert.True(await ScalarAsync<bool>(owner, "select exists (select 1 from pg_indexes where schemaname = 'public' and indexname = @index)", ("index", index)));
        }

        Assert.Equal(3L, await ScalarAsync<long>(owner, "select count(*) from pg_constraint where conrelid = 'session_items'::regclass and contype = 'f'"));
        Assert.Equal(2L, await ScalarAsync<long>(owner, """
            select count(*)
            from pg_constraint fk
            join pg_class referenced on referenced.oid = fk.confrelid
            where fk.conrelid = 'session_items'::regclass
              and fk.contype = 'f'
              and referenced.relname in ('questions', 'categories', 'media_assets')
            """));

        await InsertSessionAsync(owner, TenantA, Guid.NewGuid(), OwnerA, "catalogue");
        Assert.Equal("lobby", await ScalarAsync<string>(owner, "select status from sessions where tenant_id = @tenant and code = 'catalogue'", ("tenant", TenantA)));
        Assert.Equal(-1, await ScalarAsync<int>(owner, "select slot from sessions where tenant_id = @tenant and code = 'catalogue'", ("tenant", TenantA)));
    }

    [SkippableFact]
    public async Task Accounts_and_tenant_owned_session_tables_enforce_access_and_isolation()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into accounts (id, email) values (@id, @email)", ("id", accountA), ("email", "a@example.test"));
        await ExecuteAsync(owner, "insert into accounts (id, email) values (@id, @email)", ("id", accountB), ("email", "b@example.test"));
        await ExecuteAsync(owner, "update players set account_id = @account where tenant_id = @tenant and id = @player", ("account", accountA), ("tenant", TenantA), ("player", OwnerA));
        await ExecuteAsync(owner, "update players set account_id = @account where tenant_id = @tenant and id = @player", ("account", accountB), ("tenant", TenantB), ("player", OwnerB));
        await InsertSessionAsync(owner, TenantA, sessionA, OwnerA, "a");
        await InsertSessionAsync(owner, TenantB, sessionB, OwnerB, "b");
        await InsertParticipantAsync(owner, TenantA, sessionA, OwnerA, 0);
        await InsertParticipantAsync(owner, TenantB, sessionB, OwnerB, 0);
        await InsertItemAsync(owner, TenantA, sessionA, 0);
        await InsertItemAsync(owner, TenantB, sessionB, 0);
        await InsertAnswerAsync(owner, TenantA, sessionA, OwnerA, 0);
        await InsertAnswerAsync(owner, TenantB, sessionB, OwnerB, 0);

        await using var app = await OpenAsync("quesshi_app");
        await SetTenantAsync(app, TenantA);
        foreach (var table in new[] { "players", "sessions", "session_participants", "session_items", "session_answers" })
            Assert.Equal(1L, await ScalarAsync<long>(app, $"select count(*) from {table}"));

        var appPlayer = Guid.NewGuid();
        var appSession = Guid.NewGuid();
        await InsertPlayerAsync(app, TenantA, appPlayer);
        await InsertSessionAsync(app, TenantA, appSession, appPlayer, "app-write");
        await InsertParticipantAsync(app, TenantA, appSession, appPlayer, 0);
        await InsertItemAsync(app, TenantA, appSession, 0);
        await InsertAnswerAsync(app, TenantA, appSession, appPlayer, 0);

        Assert.Equal(0L, await ScalarAsync<long>(app, "select count(*) from accounts where id = @id", ("id", accountB)));
        await ExecuteAsync(app, "update players set account_id = @account where tenant_id = @tenant and id = @player", ("account", accountB), ("tenant", TenantA), ("player", appPlayer));
        Assert.Equal(1L, await ScalarAsync<long>(app, "select count(*) from accounts where id = @id", ("id", accountB)));
        var accountC = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into accounts (id, email) values (@id, 'c@example.test')", ("id", accountC));
        Assert.Equal(0L, await ScalarAsync<long>(app, "select count(*) from accounts where id = @id", ("id", accountC)));
        await ExecuteAsync(app, "insert into players (tenant_id, id, account_id, display_name, avatar_seed, lang) values (@tenant, @id, @account, 'linked', 'seed', 'en')", ("tenant", TenantA), ("id", Guid.NewGuid()), ("account", accountC));
        Assert.Equal(1L, await ScalarAsync<long>(app, "select count(*) from accounts where id = @id", ("id", accountC)));
        await AssertSqlStateAsync(() => InsertPlayerAsync(app, TenantB, Guid.NewGuid()), "42501");
        await AssertSqlStateAsync(() => InsertSessionAsync(app, TenantB, Guid.NewGuid(), OwnerB, "cross-tenant"), "42501");
        await AssertSqlStateAsync(() => InsertParticipantAsync(app, TenantB, sessionB, OwnerB, 1), "42501");
        await AssertSqlStateAsync(() => InsertItemAsync(app, TenantB, sessionB, 1), "42501");
        await AssertSqlStateAsync(() => InsertAnswerAsync(app, TenantB, sessionB, OwnerB, 0, 1), "42501");
        Assert.Equal(3L, await ScalarAsync<long>(app, "select count(*) from accounts"));
        await AssertSqlStateAsync(() => ExecuteAsync(app, "insert into accounts (id, email) values (@id, 'forbidden@example.test')", ("id", Guid.NewGuid())), "42501");
        await AssertSqlStateAsync(() => ExecuteAsync(app, "update accounts set email = 'forbidden@example.test'"), "42501");

        await using var unbound = await OpenAsync("quesshi_app");
        foreach (var table in new[] { "players", "sessions", "session_participants", "session_items", "session_answers" })
            await Assert.ThrowsAsync<PostgresException>(async () => _ = await ScalarAsync<long>(unbound, $"select count(*) from {table}"));

        await using var system = await OpenAsync("quesshi_system");
        foreach (var table in new[] { "players", "sessions", "session_participants", "session_items", "session_answers" })
            await AssertSqlStateAsync(() => ScalarAsync<long>(system, $"select count(*) from {table}"), "42501");
        var systemAccount = Guid.NewGuid();
        await ExecuteAsync(system, "insert into accounts (id, email) values (@id, 'system@example.test')", ("id", systemAccount));
        await ExecuteAsync(system, "update accounts set email = 'updated-system@example.test' where id = @id", ("id", systemAccount));
        Assert.Equal(4L, await ScalarAsync<long>(system, "select count(*) from accounts"));
        Assert.Equal("updated-system@example.test", await ScalarAsync<string>(system, "select email from accounts where id = @id", ("id", systemAccount)));

        await AssertSqlStateAsync(() => ExecuteAsync(owner, "insert into players (tenant_id, id, account_id, display_name, avatar_seed, lang) values (@tenant, @id, @account, 'duplicate', 'seed', 'en')", ("tenant", TenantA), ("id", Guid.NewGuid()), ("account", accountA)), "23505");
        await ExecuteAsync(owner, "insert into players (tenant_id, id, account_id, display_name, avatar_seed, lang) values (@tenant, @id, @account, 'same-account-other-tenant', 'seed', 'en')", ("tenant", TenantB), ("id", Guid.NewGuid()), ("account", accountA));
        await ExecuteAsync(owner, "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @id, 'guest-1', 'seed', 'en'), (@tenant, @other, 'guest-2', 'seed', 'en')", ("tenant", TenantA), ("id", Guid.NewGuid()), ("other", Guid.NewGuid()));
        await AssertSqlStateAsync(() => ExecuteAsync(owner, "insert into players (tenant_id, id, account_id, display_name, avatar_seed, lang) values (@tenant, @id, @account, 'missing', 'seed', 'en')", ("tenant", TenantA), ("id", Guid.NewGuid()), ("account", Guid.NewGuid())), "23503");
        await AssertSqlStateAsync(() => ExecuteAsync(owner, "insert into accounts (id, email, deleted_at) values (@id, 'still-present@example.test', now())", ("id", Guid.NewGuid())), "23514");
        await ExecuteAsync(owner, "insert into accounts (id) values (@id)", ("id", Guid.NewGuid()));
        await ExecuteAsync(owner, "insert into accounts (id, deleted_at) values (@id, now())", ("id", Guid.NewGuid()));
    }

    [SkippableFact]
    public async Task Session_tables_reject_every_invalid_check_and_allow_boundary_rows()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var session = Guid.NewGuid();
        await InsertSessionAsync(owner, TenantA, session, OwnerA, "checks");

        foreach (var sql in new[]
        {
            "update sessions set status = 'bad' where id = @session", "update sessions set capacity = 0 where id = @session",
            "update sessions set content = '[]'::jsonb where id = @session", "update sessions set rules = '[]'::jsonb where id = @session",
            "update sessions set rules_version = 0 where id = @session", "update sessions set slot = -2 where id = @session",
            "update sessions set step = -1 where id = @session", "update sessions set revealed_slot = -2 where id = @session",
            "update sessions set fault_count = -1 where id = @session", "update sessions set version = -1 where id = @session",
            "update sessions set state = '[]'::jsonb where id = @session", "update sessions set state_version = 0 where id = @session",
            "update sessions set result = '[]'::jsonb where id = @session", "update sessions set result_version = 0 where id = @session",
            "update sessions set rematch_of = id where id = @session", "update sessions set status = 'running' where id = @session"
        }) await AssertSqlStateAsync(() => ExecuteAsync(owner, sql, ("session", session)), "23514");

        var terminal = Guid.NewGuid();
        await InsertSessionAsync(owner, TenantA, terminal, OwnerA, "terminal");
        await AssertSqlStateAsync(() => ExecuteAsync(owner, "update sessions set status = 'finished', end_reason = 'complete', ended_at = now() where id = @session", ("session", terminal)), "23514");
        await ExecuteAsync(owner, "update sessions set status = 'finished', format = 'async_duel', end_reason = 'forfeit', ended_at = now() where id = @session", ("session", terminal));
        foreach (var sql in new[]
        {
            "update sessions set ended_at = null where id = @session", "update sessions set state = '{\"work\":true}'::jsonb where id = @session",
            "update sessions set due_at = now() where id = @session"
        }) await AssertSqlStateAsync(() => ExecuteAsync(owner, sql, ("session", terminal)), "23514");

        await InsertParticipantAsync(owner, TenantA, session, OwnerA, 0);
        foreach (var sql in new[]
        {
            "update session_participants set seat = -1 where session_id = @session", "update session_participants set role = 'bad' where session_id = @session",
            "update session_participants set status = 'bad' where session_id = @session", "update session_participants set slot = -2 where session_id = @session",
            "update session_participants set step = -1 where session_id = @session", "update session_participants set revealed_slot = -2 where session_id = @session",
            "update session_participants set place = 0 where session_id = @session", "update session_participants set outcome = 'bad' where session_id = @session",
            "update session_participants set state = '[]'::jsonb where session_id = @session", "update session_participants set result = '[]'::jsonb where session_id = @session"
        }) await AssertSqlStateAsync(() => ExecuteAsync(owner, sql, ("session", session)), "23514");

        await InsertItemAsync(owner, TenantA, session, 0);
        foreach (var sql in new[]
        {
            "update session_items set slot = -1 where session_id = @session", "update session_items set kind_version = 0 where session_id = @session",
            "update session_items set payload = '[]'::jsonb where session_id = @session", "update session_items set resolved = '[]'::jsonb where session_id = @session",
            "update session_items set card = '[]'::jsonb where session_id = @session", "update session_items set reveal = '[]'::jsonb where session_id = @session",
            "update session_items set level = 0 where session_id = @session", "update session_items set category = '[]'::jsonb where session_id = @session"
        }) await AssertSqlStateAsync(() => ExecuteAsync(owner, sql, ("session", session)), "23514");

        await InsertAnswerAsync(owner, TenantA, session, OwnerA, 0);
        foreach (var sql in new[]
        {
            "update session_answers set seq = -1 where session_id = @session", "update session_answers set revision = -1 where session_id = @session",
            "update session_answers set credit = -0.1 where session_id = @session", "update session_answers set taken_ms = -1 where session_id = @session"
        }) await AssertSqlStateAsync(() => ExecuteAsync(owner, sql, ("session", session)), "23514");
        await InsertAnswerAsync(owner, TenantA, session, OwnerA, 0, 1, 1, 1, 0);
    }

    [SkippableFact]
    public async Task Session_relationships_indexes_and_cascades_are_enforced()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var first = Guid.NewGuid();
        await InsertSessionAsync(owner, TenantA, first, OwnerA, "shared-code");
        await AssertSqlStateAsync(() => InsertSessionAsync(owner, TenantA, Guid.NewGuid(), OwnerA, "shared-code"), "23505");
        var finished = Guid.NewGuid();
        await InsertSessionAsync(owner, TenantA, finished, OwnerA, "shared-code", "cancelled", ended: true);
        await InsertSessionAsync(owner, TenantA, Guid.NewGuid(), OwnerA, "shared-code", "cancelled", ended: true);
        await AssertSqlStateAsync(() => ExecuteAsync(owner, "update sessions set rematch_of = id where id = @session", ("session", first)), "23514");
        var rematch = Guid.NewGuid();
        await InsertSessionAsync(owner, TenantA, rematch, OwnerA, "rematch");
        await ExecuteAsync(owner, "update sessions set rematch_of = @first where id = @session", ("first", first), ("session", rematch));
        var duplicateRematch = Guid.NewGuid();
        await InsertSessionAsync(owner, TenantA, duplicateRematch, OwnerA, "duplicate-rematch");
        await AssertSqlStateAsync(() => ExecuteAsync(owner, "update sessions set rematch_of = @first where id = @session", ("first", first), ("session", duplicateRematch)), "23505");

        await InsertParticipantAsync(owner, TenantA, first, OwnerA, 0);
        var otherPlayer = Guid.NewGuid();
        await InsertPlayerAsync(owner, TenantA, otherPlayer);
        await AssertSqlStateAsync(() => InsertParticipantAsync(owner, TenantA, first, otherPlayer, 0), "23505");
        await InsertItemAsync(owner, TenantA, first, 0);
        await AssertSqlStateAsync(() => InsertAnswerAsync(owner, TenantA, first, OwnerA, 1), "23503");
        await AssertSqlStateAsync(() => InsertAnswerAsync(owner, TenantA, first, otherPlayer, 0), "23503");
        await InsertAnswerAsync(owner, TenantA, first, OwnerA, 0);

        await ExecuteAsync(owner, "delete from sessions where tenant_id = @tenant and id in (@rematch, @duplicate)", ("tenant", TenantA), ("rematch", rematch), ("duplicate", duplicateRematch));
        await ExecuteAsync(owner, "delete from sessions where tenant_id = @tenant and id = @session", ("tenant", TenantA), ("session", first));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from session_participants where session_id = @session", ("session", first)));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from session_items where session_id = @session", ("session", first)));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from session_answers where session_id = @session", ("session", first)));
        await AssertSqlStateAsync(() => InsertSessionAsync(owner, TenantA, Guid.NewGuid(), OwnerB, "cross-tenant-owner"), "23503");
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = await OpenAsync("quesshi_owner");
        await ExecuteAsync(owner, "delete from sessions; delete from tenants");
        await ExecuteAsync(owner, "delete from accounts");
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@id, @slug, @slug, 'en'), (@other, @otherSlug, @otherSlug, 'en')", ("id", TenantA), ("slug", "session-a"), ("other", TenantB), ("otherSlug", "session-b"));
        await InsertPlayerAsync(owner, TenantA, OwnerA);
        await InsertPlayerAsync(owner, TenantB, OwnerB);
        return owner;
    }

    private static async Task InsertPlayerAsync(NpgsqlConnection connection, Guid tenant, Guid player) =>
        await ExecuteAsync(connection, "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @player, 'Player', 'seed', 'en')", ("tenant", tenant), ("player", player));

    private static async Task InsertSessionAsync(NpgsqlConnection connection, Guid tenant, Guid session, Guid owner, string code, string status = "lobby", bool ended = false) =>
        await ExecuteAsync(connection, """
            insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, ended_at)
            values (@tenant, @session, @code, 'trivia', @owner, 'en', @status, 1, '{}'::jsonb, case when @ended then now() else null end)
            """, ("tenant", tenant), ("session", session), ("code", code), ("owner", owner), ("status", status), ("ended", ended));

    private static async Task InsertParticipantAsync(NpgsqlConnection connection, Guid tenant, Guid session, Guid player, short seat) =>
        await ExecuteAsync(connection, "insert into session_participants (tenant_id, session_id, player_id, seat) values (@tenant, @session, @player, @seat)", ("tenant", tenant), ("session", session), ("player", player), ("seat", seat));

    private static async Task InsertItemAsync(NpgsqlConnection connection, Guid tenant, Guid session, int slot) =>
        await ExecuteAsync(connection, """
            insert into session_items (tenant_id, session_id, slot, kind, kind_version, prompt, payload, card, reveal, level)
            values (@tenant, @session, @slot, 'trivia', 1, 'Question', '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, 1)
            """, ("tenant", tenant), ("session", session), ("slot", slot));

    private static async Task InsertAnswerAsync(NpgsqlConnection connection, Guid tenant, Guid session, Guid player, int slot, int seq = 0, int revision = 0, float credit = 0, int takenMs = 0) =>
        await ExecuteAsync(connection, """
            insert into session_answers (tenant_id, session_id, slot, player_id, seq, revision, credit, taken_ms, received_at)
            values (@tenant, @session, @slot, @player, @seq, @revision, @credit, @takenMs, now())
            """, ("tenant", tenant), ("session", session), ("slot", slot), ("player", player), ("seq", seq), ("revision", revision), ("credit", credit), ("takenMs", takenMs));

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static Task SetTenantAsync(NpgsqlConnection connection, Guid tenant) =>
        ExecuteAsync(connection, "select set_config('app.tenant_id', @tenant::text, false)", ("tenant", tenant));

    private static async Task AssertSqlStateAsync(Func<Task> action, string sqlState)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(sqlState, exception.SqlState);
    }

    private static async Task AssertSqlStateAsync<T>(Func<Task<T>> action, string sqlState)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(async () => _ = await action());
        Assert.Equal(sqlState, exception.SqlState);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
