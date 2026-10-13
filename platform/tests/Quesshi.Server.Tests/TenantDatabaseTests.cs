using System.Data;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed partial class TenantDatabaseTests(PostgresFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("63f188c2-f5a7-4c12-a4ef-d20db7eb9ce1");
    private static readonly Guid TenantB = Guid.Parse("e1c56186-6e74-43a1-bf29-0185cd68c5b9");

    [Fact]
    public void Plugin_database_contracts_do_not_publish_provider_handles()
    {
        var exposedTypes = typeof(ITenantDb).Assembly.GetExportedTypes();
        var signatures = exposedTypes.SelectMany(type =>
            type.GetMethods().SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType))
            .Concat(type.GetProperties().Select(property => property.PropertyType)));

        Assert.DoesNotContain(signatures, type => type.FullName is "Npgsql.NpgsqlConnection" or "Npgsql.NpgsqlTransaction" or "Npgsql.NpgsqlDataSource");
        Assert.DoesNotContain(typeof(TenantDb).Assembly.GetExportedTypes(), type => type.Name == "ISystemDb");
    }

    [Fact]
    public async Task Database_sources_pin_runtime_roles_and_pool_sizes()
    {
        await using var sources = new DatabaseSources(
            "Host=localhost;Database=quesshi;Username=migration_owner;Maximum Pool Size=1",
            "Host=localhost;Database=quesshi;Username=migration_owner;Maximum Pool Size=1");

        var app = new NpgsqlConnectionStringBuilder(Source(sources, "App").ConnectionString);
        var system = new NpgsqlConnectionStringBuilder(Source(sources, "System").ConnectionString);

        Assert.Equal("quesshi_app", app.Username);
        Assert.Equal(40, app.MaxPoolSize);
        Assert.Equal("quesshi_system", system.Username);
        Assert.Equal(10, system.MaxPoolSize);
    }

    [SkippableFact]
    public async Task Tenant_transaction_scopes_queries_rolls_back_and_does_not_leak_pool_state()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var aRow = Guid.NewGuid();
        var bRow = Guid.NewGuid();

        await database.InTenantAsync(TenantA, tx => tx.ExecuteAsync(
            "insert into tenant_rows (tenant_id, id, value) values (@tenantId, @id, @value)",
            new { tenantId = TenantA, id = aRow, value = "a" }));
        await database.InTenantAsync(TenantB, tx => tx.ExecuteAsync(
            "insert into tenant_rows (tenant_id, id, value) values (@tenantId, @id, @value)",
            new { tenantId = TenantB, id = bRow, value = "b" }));

        var aRows = await database.InTenantAsync(TenantA, tx => tx.QueryAsync<Guid>("select id from tenant_rows order by id"));
        var bRows = await database.InTenantAsync(TenantB, tx => tx.QueryAsync<Guid>("select id from tenant_rows order by id"));
        Assert.Equal([aRow], aRows);
        Assert.Equal([bRow], bRows);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InTenantAsync<int>(TenantA, async tx =>
        {
            await tx.ExecuteAsync("insert into tenant_rows (tenant_id, id, value) values (@tenantId, @id, @value)", new { tenantId = TenantA, id = Guid.NewGuid(), value = "rollback" });
            throw new InvalidOperationException("rollback");
        }));
        Assert.Equal(1, await database.InTenantAsync(TenantA, async tx => (await tx.QueryAsync<Guid>("select id from tenant_rows")).Count));

        await using var outside = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_app"));
        await outside.OpenAsync();
        var outsideFailure = await Assert.ThrowsAsync<PostgresException>(async () => await new NpgsqlCommand("select count(*) from tenant_rows", outside).ExecuteScalarAsync());
        Assert.Equal("42704", outsideFailure.SqlState);
        Assert.Contains("unrecognized configuration parameter", outsideFailure.Message);
        await using var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await system.OpenAsync();
        var systemFailure = await Assert.ThrowsAsync<PostgresException>(async () => await new NpgsqlCommand("select count(*) from tenant_rows", system).ExecuteScalarAsync());
        Assert.Equal("42501", systemFailure.SqlState);
        var crossTenantFailure = await Assert.ThrowsAsync<PostgresException>(() => database.InTenantAsync(TenantA, tx => tx.ExecuteAsync(
            "insert into tenant_rows (tenant_id, id, value) values (@tenantId, @id, @value)",
            new { tenantId = TenantB, id = Guid.NewGuid(), value = "forbidden" })));
        Assert.Equal("42501", crossTenantFailure.SqlState);
    }

    [SkippableFact]
    public async Task Tenant_helpers_share_the_requested_transaction_and_roll_back()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var row = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InTenantAsync<int>(TenantA, async tx =>
        {
            Assert.Equal("serializable", await tx.QuerySingleOrDefaultAsync<string>("show transaction_isolation"));
            Assert.Equal(TenantA, await tx.QuerySingleOrDefaultAsync<Guid>("select current_setting('app.tenant_id')::uuid"));
            await tx.ExecuteAsync(
                "insert into tenant_rows (tenant_id, id, value) values (@tenantId, @id, @value)",
                new { tenantId = TenantA, id = row, value = "uncommitted" });
            Assert.Contains(row, await tx.QueryAsync<Guid>("select id from tenant_rows where id = @row", new { row }));
            Assert.Equal(row, await tx.QuerySingleOrDefaultAsync<Guid>("select id from tenant_rows where id = @row", new { row }));
            throw new InvalidOperationException("rollback");
        }, IsolationLevel.Serializable));

        Assert.Null(await database.InTenantAsync(TenantA, tx => tx.QuerySingleOrDefaultAsync<Guid?>(
            "select id from tenant_rows where id = @row", new { row })));
    }

    [SkippableFact]
    public async Task Retries_only_transient_failures_and_keeps_failed_attempts_uncommitted()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var attempts = 0;
        var row = Guid.NewGuid();

        await database.InTenantAsync(TenantA, async tx =>
        {
            attempts++;
            await tx.ExecuteAsync("insert into tenant_rows (tenant_id, id, value) values (@tenantId, @id, @value)", new { tenantId = TenantA, id = row, value = "retry" });
            if (attempts == 1) await tx.ExecuteAsync("do $$ begin raise exception using errcode = '40001'; end $$");
            return 0;
        }, IsolationLevel.Serializable);
        Assert.Equal(2, attempts);
        Assert.Equal(1, await database.InTenantAsync(TenantA, async tx => (await tx.QueryAsync<Guid>("select id from tenant_rows where id = @row", new { row })).Count));

        attempts = 0;
        var transient = await Assert.ThrowsAsync<PostgresException>(() => database.InTenantAsync(TenantA, async tx =>
        {
            attempts++;
            await tx.ExecuteAsync("do $$ begin raise exception using errcode = '40P01'; end $$");
            return 0;
        }));
        Assert.Equal("40P01", transient.SqlState);
        Assert.Equal(3, attempts);

        attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InTenantAsync<int>(TenantA, _ =>
        {
            attempts++;
            throw new InvalidOperationException("not transient");
        }));
        Assert.Equal(1, attempts);
    }

    [SkippableFact]
    public async Task Dapper_helpers_round_trip_postgres_types_and_jsonb()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var id = Guid.NewGuid();
        var payload = new Payload(["éclair", "東京"], new PayloadChild("nested", [1, 2, 3]));
        var instant = new DateTimeOffset(2026, 10, 13, 8, 30, 0, TimeSpan.FromHours(3));

        await database.InTenantAsync(TenantA, tx => tx.ExecuteAsync(
            "insert into typed_rows (tenant_id, id, guid_value, happened_at, payload) values (@tenantId, @id, @id, @instant, @payload)",
            new { tenantId = TenantA, id, instant, payload = new Jsonb<Payload>(payload, TenantJsonContext.Default.Payload) }));
        using var documentParameter = JsonDocument.Parse("{\"name\":\"東京\"}");
        var elementParameter = documentParameter.RootElement.Clone();
        var parameterRoundTrip = await database.InTenantAsync(TenantA, async tx => new
        {
            Element = await tx.QuerySingleOrDefaultAsync<JsonElement>("select cast(@element as jsonb)", new { element = elementParameter }),
            Document = await tx.QuerySingleOrDefaultAsync<JsonDocument>("select cast(@document as jsonb)", new { document = documentParameter })
        });
        var read = await database.InTenantAsync(TenantA, async tx => new
        {
            Guid = await tx.QuerySingleOrDefaultAsync<Guid>("select guid_value from typed_rows where id = @id", new { id }),
            Instant = await tx.QuerySingleOrDefaultAsync<DateTimeOffset>("select happened_at from typed_rows where id = @id", new { id }),
            Json = await tx.QuerySingleOrDefaultAsync<JsonDocument>("select payload from typed_rows where id = @id", new { id }),
            Count = (await tx.QueryAsync<Guid>("select id from typed_rows where id = any(@ids)", new { ids = new[] { id, Guid.NewGuid() } })).Count
        });

        Assert.Equal(id, read.Guid);
        Assert.Equal(instant.ToUniversalTime(), read.Instant);
        var roundTrippedPayload = read.Json is null ? null : JsonSerializer.Deserialize(read.Json, TenantJsonContext.Default.Payload);
        Assert.NotNull(roundTrippedPayload);
        Assert.Equal(payload.Labels, roundTrippedPayload.Labels);
        Assert.Equal(payload.Child.Name, roundTrippedPayload.Child.Name);
        Assert.Equal(payload.Child.Values, roundTrippedPayload.Child.Values);
        Assert.Equal("東京", parameterRoundTrip.Element.GetProperty("name").GetString());
        Assert.Equal("東京", parameterRoundTrip.Document?.RootElement.GetProperty("name").GetString());
        Assert.Equal(1, read.Count);
    }

    [SkippableFact]
    public async Task Locks_follow_the_global_order_and_inbox_flushes_once_in_player_order()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources, new TestInboxSink());
        var locks = new TenantLocks();
        var inbox = new TenantInbox();
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var content = Guid.NewGuid();
        var owned = Guid.NewGuid();
        await SeedAsync(playerA, playerB, content, owned);

        await database.InTenantAsync(TenantA, async tx =>
        {
            await locks.ContentAsync(tx, [content]);
            await Assert.ThrowsAsync<LockOrderViolation>(() => locks.PlayersAsync(tx, [playerA]));
            return 0;
        });
        var orderedPlayers = new[] { playerA, playerB }.Order().ToArray();
        await database.InTenantAsync(TenantA, async tx =>
        {
            await locks.PlayersAsync(tx, [orderedPlayers[1]]);
            await Assert.ThrowsAsync<LockOrderViolation>(() => locks.PlayersAsync(tx, [orderedPlayers[0]]));
            return 0;
        });
        await database.InTenantAsync(TenantA, async tx =>
        {
            await locks.PlayersAsync(tx, [playerB, playerA]);
            await locks.PlayersAsync(tx, [playerA]);
            await locks.ContentAsync(tx, [content]);
            await locks.OwnAsync(tx, "plugin_rows", [owned]);
            inbox.Changed(tx, playerB);
            inbox.Changed(tx, playerA);
            inbox.Changed(tx, playerB);
            return 0;
        });

        var order = await database.InTenantAsync(TenantA, tx => tx.QueryAsync<Guid>("select player_id from inbox_events order by sequence"));
        Assert.Equal(orderedPlayers, order);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InTenantAsync<int>(TenantA, tx =>
        {
            inbox.Changed(tx, playerA);
            throw new InvalidOperationException("rollback");
        }));
        Assert.Equal(2, await database.InTenantAsync(TenantA, async tx => (await tx.QueryAsync<Guid>("select player_id from inbox_events")).Count));

        var retryAttempts = 0;
        await database.InTenantAsync(TenantA, async tx =>
        {
            retryAttempts++;
            inbox.Changed(tx, retryAttempts == 1 ? playerA : playerB);
            if (retryAttempts == 1) await tx.ExecuteAsync("do $$ begin raise exception using errcode = '40001'; end $$");
            return 0;
        });
        var afterRetry = await database.InTenantAsync(TenantA, tx => tx.QueryAsync<Guid>("select player_id from inbox_events order by sequence"));
        Assert.Equal([.. orderedPlayers, playerB], afterRetry);
    }

    [SkippableFact]
    public async Task Repeated_content_and_unmatched_lock_requests_do_not_violate_order()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var locks = new TenantLocks();
        var missingPlayer = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var player = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var question = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var pack = Guid.Parse("00000000-0000-0000-0000-000000000020");
        var owned = Guid.Parse("00000000-0000-0000-0000-000000000030");
        await SeedAsync(player, Guid.NewGuid(), question, owned);
        await using (var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner")))
        {
            await owner.OpenAsync();
            await ExecuteAsync(owner, "insert into packs (tenant_id, id) values (@tenant, @id)", ("tenant", TenantA), ("id", pack));
        }

        await database.InTenantAsync(TenantA, async tx =>
        {
            await locks.PlayersAsync(tx, [missingPlayer]);
            await locks.PlayersAsync(tx, [player]);
            await locks.PlayersAsync(tx, [missingPlayer]);
            await locks.ContentAsync(tx, [question, pack]);
            await locks.ContentAsync(tx, [question, pack]);
            await locks.ContentAsync(tx, [question]);
            await locks.OwnAsync(tx, "plugin_rows", [owned]);
            await locks.ContentAsync(tx, [question, pack]);
            return 0;
        });
    }

    [SkippableFact]
    public async Task Player_lock_order_avoids_deadlock_and_does_not_block_foreign_key_inserts()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var locks = new TenantLocks();
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        await SeedAsync(playerA, playerB, Guid.NewGuid(), Guid.NewGuid());

        await Task.WhenAll(
            database.InTenantAsync(TenantA, async tx => { await locks.PlayersAsync(tx, [playerA, playerB]); await Task.Delay(50); return 0; }),
            database.InTenantAsync(TenantA, async tx => { await locks.PlayersAsync(tx, [playerB, playerA]); await Task.Delay(50); return 0; }));

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var locker = database.InTenantAsync(TenantA, async tx =>
        {
            await locks.PlayersAsync(tx, [playerA]);
            held.SetResult();
            await release.Task;
            return 0;
        });
        await held.Task;
        var insert = database.InTenantAsync(TenantA, tx => tx.ExecuteAsync(
            "insert into player_references (tenant_id, id, player_id) values (@tenantId, @id, @playerId)",
            new { tenantId = TenantA, id = Guid.NewGuid(), playerId = playerA }));
        Assert.Same(insert, await Task.WhenAny(insert, Task.Delay(TimeSpan.FromSeconds(2))));
        release.SetResult();
        await locker;
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static NpgsqlDataSource Source(DatabaseSources sources, string name) =>
        (NpgsqlDataSource)(typeof(DatabaseSources).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sources)
            ?? throw new InvalidOperationException($"Missing {name} data source."));

    private async Task PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();

        foreach (var table in new[]
                 {
                     "player_references", "inbox_events", "plugin_rows", "packs",
                     "questions", "typed_rows", "tenant_rows"
                 })
        {
            await ExecuteAsync(owner, $"drop table if exists {table} cascade");
        }

        foreach (var statement in new[]
        {
            "create table tenant_rows (tenant_id uuid not null, id uuid not null, value text not null, primary key (tenant_id, id))",
            "create table typed_rows (tenant_id uuid not null, id uuid not null, guid_value uuid not null, happened_at timestamptz not null, payload jsonb not null, primary key (tenant_id, id))",
            "create table questions (tenant_id uuid not null, id uuid not null, primary key (tenant_id, id))",
            "create table packs (tenant_id uuid not null, id uuid not null, primary key (tenant_id, id))",
            "create table plugin_rows (tenant_id uuid not null, id uuid not null, primary key (tenant_id, id))",
            "create table player_references (tenant_id uuid not null, id uuid not null, player_id uuid not null, primary key (tenant_id, id), foreign key (tenant_id, player_id) references players (tenant_id, id))",
            "create table inbox_events (tenant_id uuid not null, sequence bigserial not null, player_id uuid not null, primary key (tenant_id, sequence))"
        }) await ExecuteAsync(owner, statement);
        foreach (var table in new[] { "tenant_rows", "typed_rows", "questions", "packs", "plugin_rows", "player_references", "inbox_events" })
        {
            await ExecuteAsync(
                owner,
                "select quesshi_tenant_table(@tableName::regclass, @owner)",
                ("tableName", table),
                ("owner", table == "plugin_rows" ? "demo_plugin" : "core"));
        }
        await ExecuteAsync(owner, "delete from sessions; delete from tenants");
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'tenant-a', 'Tenant A', 'en'), (@b, 'tenant-b', 'Tenant B', 'en')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "grant usage, select on sequence inbox_events_sequence_seq to quesshi_app");
    }

    private async Task SeedAsync(Guid playerA, Guid playerB, Guid content, Guid owned)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        foreach (var (table, id) in new[] { ("questions", content), ("plugin_rows", owned) })
        {
            await ExecuteAsync(owner, $"insert into {table} (tenant_id, id) values (@tenant, @id)", ("tenant", TenantA), ("id", id));
        }
        await ExecuteAsync(owner, "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @a, 'Player A', 'seed-a', 'en'), (@tenant, @b, 'Player B', 'seed-b', 'en')", ("tenant", TenantA), ("a", playerA), ("b", playerB));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestInboxSink : IInboxBumpSink
    {
        public Task ApplyAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken = default) => transaction.ExecuteAsync(
            "insert into inbox_events (tenant_id, player_id) values (current_setting('app.tenant_id')::uuid, @playerId)",
            new { playerId }, cancellationToken);
    }

}

internal sealed record Payload(string[] Labels, PayloadChild Child);

internal sealed record PayloadChild(string Name, int[] Values);

[JsonSerializable(typeof(Payload))]
internal sealed partial class TenantJsonContext : JsonSerializerContext;
