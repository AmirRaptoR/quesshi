using Microsoft.AspNetCore.Http;
using Npgsql;
using Quesshi.Application;
using Quesshi.Application.Jobs;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Server.Accounts;
using Quesshi.Server.Security;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AccountDeletionDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Deletion_revokes_every_account_session_and_durably_queues_each_live_player()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = Sources();
        var sessions = new PlayerSessionService(sources, new TenantDb(sources), new TestClock(DateTimeOffset.UnixEpoch));
        var first = new DefaultHttpContext();
        var second = new DefaultHttpContext();
        await sessions.IssueAccountAsync(first, seeded.FirstTenant, seeded.Account);
        await sessions.IssueAccountAsync(second, seeded.SecondTenant, seeded.Account);
        var notifier = new RecordingNotifier();
        var deletion = new AccountDeletionService(new SystemDb(sources), notifier);

        Assert.True(await deletion.DeleteAsync(seeded.Account));

        Assert.Null(await ScalarAsync<string?>("select email::text from accounts where id = @account", ("account", seeded.Account)));
        Assert.True(await ScalarAsync<bool>("select deleted_at is not null from accounts where id = @account", ("account", seeded.Account)));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from auth_sessions where account_id = @account", ("account", seeded.Account)));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from account_identities where account_id = @account", ("account", seeded.Account)));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from login_codes where email = @email", ("email", seeded.Email)));
        Assert.Equal(2L, await ScalarAsync<long>("select count(*) from jobs where kind = 'account-player-cleanup' and owner = 'core' and max_attempts is null"));
        Assert.Null(await sessions.ResolveAsync(seeded.FirstTenant, CookieValue(first)));
        Assert.Null(await sessions.ResolveAsync(seeded.SecondTenant, CookieValue(second)));
        var replacement = Guid.NewGuid();
        await using (var connection = await OpenAsync("quesshi_system"))
            await ExecuteAsync(connection, "insert into accounts (id, email) values (@account, @email)", ("account", replacement), ("email", seeded.Email));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from account_players(@account)", ("account", replacement)));
        Assert.Equal(2, notifier.Players.Count);
        Assert.Contains(notifier.Players, player => player == new AccountDeletionPlayer(seeded.FirstTenant, seeded.FirstPlayer));
        Assert.Contains(notifier.Players, player => player == new AccountDeletionPlayer(seeded.SecondTenant, seeded.SecondPlayer));
    }

    [SkippableFact]
    public async Task Core_cleanup_scrubs_before_it_makes_installed_plugin_cleanup_runnable_and_keeps_history()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = Sources();
        var deletion = new AccountDeletionService(new SystemDb(sources), new RecordingNotifier());
        await deletion.DeleteAsync(seeded.Account);
        var feature = new CleanupFeature("installed-but-disabled", retired: true);
        var registry = FeatureRegistry.Create([feature]);
        var effects = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), new NullSessionEffectsNotifier());
        var cleanup = new AccountPlayerCleanup(new TenantLocks(), effects);
        registry.Jobs.Register("core", "account-player-cleanup", cleanup.HandleAsync);
        registry.Jobs.Register("core", "cleanup-player", effects.HandleCleanupJobAsync);
        var worker = new JobWorker(new PostgresJobStore(sources), registry.Jobs, new TenantDb(sources), new OffCapabilities());

        await worker.RunPassAsync(2, TimeSpan.FromMinutes(1));

        Assert.Equal(AccountPlayerCleanup.DeletedDisplayName, await TenantScalarAsync<string>(seeded.FirstTenant, "select display_name from players where id = @player", new { player = seeded.FirstPlayer }));
        Assert.Equal(AccountPlayerCleanup.DeletedAvatarSeed, await TenantScalarAsync<string>(seeded.FirstTenant, "select avatar_seed from players where id = @player", new { player = seeded.FirstPlayer }));
        Assert.True(await TenantScalarAsync<bool>(seeded.FirstTenant, "select deleted_at is not null from players where id = @player", new { player = seeded.FirstPlayer }));
        Assert.Equal("Other", await TenantScalarAsync<string>(seeded.FirstTenant, "select display_name from players where id = @player", new { player = seeded.OtherPlayer }));
        Assert.False(await TenantScalarAsync<bool>(seeded.FirstTenant, "select deleted_at is not null from players where id = @player", new { player = seeded.OtherPlayer }));
        Assert.Equal(1L, await TenantScalarAsync<long>(seeded.FirstTenant, "select count(*) from session_participants where player_id = @player", new { player = seeded.FirstPlayer }));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from jobs where tenant_id = @tenant and kind = 'cleanup-player' and status = 'ready'", ("tenant", seeded.FirstTenant)));

        await worker.RunPassAsync(2, TimeSpan.FromMinutes(1));
        Assert.Equal(1L, await TenantScalarAsync<long>(seeded.FirstTenant, "select count(*) from plugin_effects where plugin = @plugin and event_key = @key",
            new { plugin = "cleanup:installed-but-disabled", key = $"player:{seeded.FirstPlayer}" }));
    }

    private async Task<(Guid Account, Guid FirstTenant, Guid SecondTenant, Guid FirstPlayer, Guid SecondPlayer, Guid OtherPlayer, string Email)> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var account = Guid.NewGuid();
        var firstTenant = Guid.NewGuid();
        var secondTenant = Guid.NewGuid();
        var firstPlayer = Guid.NewGuid();
        var secondPlayer = Guid.NewGuid();
        var otherPlayer = Guid.NewGuid();
        var email = $"delete-{account:N}@example.test";
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, "delete from auth_sessions; delete from jobs; delete from session_participants; delete from account_identities; delete from login_codes; delete from tenants; delete from accounts");
        await ExecuteAsync(connection, "insert into tenants (id, slug, name, default_language) values (@id, @slug, 'First', 'en'), (@other, @otherSlug, 'Second', 'en')",
            ("id", firstTenant), ("slug", $"first-{firstTenant:N}"), ("other", secondTenant), ("otherSlug", $"second-{secondTenant:N}"));
        await ExecuteAsync(connection, "insert into accounts (id, email) values (@account, @email); insert into account_identities (provider, subject, account_id) values ('test', @subject, @account); insert into login_codes (email, code_hash, expires_at) values (@email, decode('00', 'hex'), now() + interval '1 hour')",
            ("account", account), ("email", email), ("subject", account.ToString("N")));
        await ExecuteAsync(connection, "insert into players (tenant_id, id, account_id, display_name, avatar_seed, lang) values (@tenant, @player, @account, 'First', 'first', 'en'), (@otherTenant, @otherPlayer, @account, 'Second', 'second', 'en'), (@tenant, @unrelatedPlayer, null, 'Other', 'other', 'en')",
            ("tenant", firstTenant), ("player", firstPlayer), ("otherTenant", secondTenant), ("otherPlayer", secondPlayer), ("unrelatedPlayer", otherPlayer), ("account", account));
        await ExecuteAsync(connection, "insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, phase, started_at, ended_at) values (@tenant, @session, @code, 'test', @player, 'en', 'finished', 1, '{}'::jsonb, 'finished', now(), now()); insert into session_participants (tenant_id, session_id, player_id, seat) values (@tenant, @session, @player, 0)",
            ("tenant", firstTenant), ("session", Guid.NewGuid()), ("code", "delete01"), ("player", firstPlayer));
        return (account, firstTenant, secondTenant, firstPlayer, secondPlayer, otherPlayer, email);
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync("quesshi_system");
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? default : (T)scalar;
    }

    private async Task<T> TenantScalarAsync<T>(Guid tenant, string sql, object parameters)
    {
        await using var sources = Sources();
        return (await new TenantDb(sources).InTenantAsync(tenant, transaction => transaction.QuerySingleOrDefaultAsync<T>(sql, parameters, CancellationToken.None), readOnly: true))!;
    }

    private async Task<NpgsqlConnection> OpenAsync(string role = "quesshi_owner")
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

    private static string CookieValue(DefaultHttpContext context)
    {
        var header = Assert.Single(context.Response.Headers.SetCookie) ?? throw new InvalidOperationException("Session cookie was not issued.");
        var value = header[(header.IndexOf('=') + 1)..];
        return Uri.UnescapeDataString(value[..value.IndexOf(';')]);
    }

    private sealed class RecordingNotifier : IAccountDeletionNotifier
    {
        public List<AccountDeletionPlayer> Players { get; } = [];
        public void Deleted(IReadOnlyList<AccountDeletionPlayer> players) => Players.AddRange(players);
    }

    private sealed class OffCapabilities : ICapabilities
    {
        public Task<bool> IsOnAsync(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<TSettings> GetSettingsAsync<TSettings>(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) where TSettings : notnull => throw new NotSupportedException();
        public Task SetAsync<TSettings>(Guid tenantId, CapabilityType type, string key, bool enabled, TSettings settings, CancellationToken cancellationToken = default) where TSettings : notnull => throw new NotSupportedException();
    }

    private sealed class CleanupFeature(string id, bool retired) : IFeature
    {
        public string Id => id;
        public bool Retired => retired;
        public FeatureSettings Settings { get; } = FeatureSettings.Create(new CleanupSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) => feature.CleanupPlayer(async (transaction, playerId, cancellationToken) =>
            await transaction.ExecuteAsync("insert into plugin_effects (tenant_id, plugin, event_key) values (app_tenant(), @plugin, @key)", new { plugin = $"cleanup:{id}", key = $"player:{playerId}" }, cancellationToken));
        private sealed record CleanupSettings;
    }
}
