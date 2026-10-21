using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Server;
using Quesshi.Server.Security;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class PlayerSessionDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Account_sessions_are_hashed_tenant_bound_and_immediately_revocable()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = CreateSources();
        var sessions = new PlayerSessionService(sources, new TenantDb(sources), new TestClock(DateTimeOffset.UnixEpoch), new TenantLocks());
        var context = new DefaultHttpContext();

        await sessions.IssueAccountAsync(context, seeded.TenantA, seeded.Account);

        var token = CookieValue(context);
        Assert.Equal(32, Convert.FromBase64String(token).Length);
        await using (var system = await OpenAsync("quesshi_system"))
        {
            var matchingHash = await ScalarAsync<bool>(system, "select exists (select 1 from auth_sessions where token_hash = @hash)",
                ("hash", SHA256.HashData(Convert.FromBase64String(token))));
            Assert.True(matchingHash);
            Assert.False(await ScalarAsync<bool>(system, "select exists (select 1 from information_schema.columns where table_name = 'auth_sessions' and column_name = 'token')"));
        }
        var cookie = Assert.Single(context.Response.Headers.SetCookie);
        Assert.NotNull(cookie);
        Assert.Contains("__Host-qp=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=2592000", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(seeded.Player, (await sessions.ResolveAsync(seeded.TenantA, token))?.PlayerId);
        Assert.Null(await sessions.ResolveAsync(seeded.TenantB, token));
        await sessions.RevokeAsync(token);
        Assert.Null(await sessions.ResolveAsync(seeded.TenantA, token));

        var secondContext = new DefaultHttpContext();
        await sessions.IssueAccountAsync(secondContext, seeded.TenantA, seeded.Account);
        await using (var owner = await OpenAsync("quesshi_owner"))
        {
            await ExecuteAsync(owner, "update players set deleted_at = now() where tenant_id = @tenant and id = @player",
                ("tenant", seeded.TenantA), ("player", seeded.Player));
        }
        Assert.Null(await sessions.ResolveAsync(seeded.TenantA, CookieValue(secondContext)));
    }

    [SkippableFact]
    public async Task Account_issuance_rechecks_liveness_after_a_deletion_race()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = CreateSources();
        var sessions = new PlayerSessionService(sources, new TenantDb(sources), new TestClock(DateTimeOffset.UnixEpoch), new TenantLocks());
        await using var owner = await OpenAsync("quesshi_owner");
        await using var deletion = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, "select id from accounts where id = @account for update", deletion, ("account", seeded.Account));

        var issuance = sessions.IssueAccountAsync(new DefaultHttpContext(), seeded.TenantA, seeded.Account);
        await Task.Delay(100);
        Assert.False(issuance.IsCompleted);
        await ExecuteAsync(owner, "update accounts set email = null, deleted_at = now() where id = @account", deletion, ("account", seeded.Account));
        await deletion.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => issuance);
        await using var system = await OpenAsync("quesshi_system");
        Assert.Equal(0L, await ScalarAsync<long>(system, "select count(*) from auth_sessions where account_id = @account", ("account", seeded.Account)));
    }

    [SkippableFact]
    public async Task Account_session_issuance_cannot_outlive_a_concurrent_tenant_ban()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = CreateSources();
        var locks = new TenantLocks();
        var sessions = new PlayerSessionService(sources, new TenantDb(sources), new TestClock(DateTimeOffset.UnixEpoch), locks);
        var manager = new PlayerBanManager(new TenantDb(sources), locks, new NoopRevocationNotifier());
        await using var owner = await OpenAsync("quesshi_owner");
        await using var held = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, "select id from players where tenant_id = @tenant and id = @player for no key update", held,
            ("tenant", seeded.TenantA), ("player", seeded.Player));

        var ban = manager.BanAsync(seeded.TenantA, seeded.Player);
        await WaitForLockWaiterAsync(owner, held);
        var issuance = sessions.IssueAccountAsync(new DefaultHttpContext(), seeded.TenantA, seeded.Account);
        await held.CommitAsync();

        Assert.Equal(PlayerBanChangeStatus.Success, (await ban).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => issuance);
        await using var system = await OpenAsync("quesshi_system");
        Assert.Equal(0L, await ScalarAsync<long>(system, "select count(*) from auth_sessions where tenant_id = @tenant and account_id = @account",
            ("tenant", seeded.TenantA), ("account", seeded.Account)));
    }

    [SkippableFact]
    public async Task Data_protection_keys_are_shared_across_server_containers_and_survive_a_restart()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using (var owner = await OpenAsync("quesshi_owner"))
        {
            await ExecuteAsync(owner, "delete from data_protection_keys");
        }

        await using var sources = CreateSources();
        var tenant = Guid.NewGuid();
        string token;
        using (var first = CreateServices(sources))
        {
            token = first.GetRequiredService<PlayerAntiforgery>().Issue(tenant, null);
        }
        using (var second = CreateServices(sources))
        {
            Assert.True(second.GetRequiredService<PlayerAntiforgery>().IsValid(tenant, null, token));
        }
        using (var restarted = CreateServices(sources))
        {
            Assert.True(restarted.GetRequiredService<PlayerAntiforgery>().IsValid(tenant, null, token));
        }
    }

    private async Task<(Guid TenantA, Guid TenantB, Guid Account, Guid Player)> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var account = Guid.NewGuid();
        var player = Guid.NewGuid();
        await using var owner = await OpenAsync("quesshi_owner");
        await ExecuteAsync(owner, "delete from auth_sessions; delete from data_protection_keys; delete from session_participants; delete from sessions; delete from tenants; delete from accounts");
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'session-a', 'Session A', 'en'), (@b, 'session-b', 'Session B', 'en')", ("a", tenantA), ("b", tenantB));
        await ExecuteAsync(owner, "insert into accounts (id, email) values (@account, 'session@example.test')", ("account", account));
        await ExecuteAsync(owner, "insert into players (tenant_id, id, account_id, display_name, avatar_seed, lang) values (@tenant, @player, @account, 'Player', 'seed', 'en')", ("tenant", tenantA), ("player", player), ("account", account));
        return (tenantA, tenantB, account, player);
    }

    private DatabaseSources CreateSources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static ServiceProvider CreateServices(DatabaseSources sources)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sources);
        services.AddSingleton<IClock>(new TestClock(DateTimeOffset.UnixEpoch));
        services.AddPlayerSecurity();
        return services.BuildServiceProvider();
    }

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static string CookieValue(DefaultHttpContext context)
    {
        var header = Assert.Single(context.Response.Headers.SetCookie);
        Assert.NotNull(header);
        var value = header["__Host-qp=".Length..];
        return Uri.UnescapeDataString(value[..value.IndexOf(';')]);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        await ExecuteAsync(connection, sql, null, parameters);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitForLockWaiterAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var command = new NpgsqlCommand("select count(*) from pg_locks where not granted and locktype = 'transactionid' and transactionid = pg_current_xact_id()::xid", connection, transaction);
            if ((long)(await command.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(10);
        }

        throw new InvalidOperationException("The ban did not wait for the player lock.");
    }

    private sealed class NoopRevocationNotifier : IPlayerAccessRevocationNotifier
    {
        public void RevokePlayer(Guid tenantId, Guid playerId) { }
    }
}
