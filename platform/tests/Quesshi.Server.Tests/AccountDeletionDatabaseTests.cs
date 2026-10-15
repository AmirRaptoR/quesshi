using Microsoft.AspNetCore.Http;
using Npgsql;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Quesshi.Application;
using Quesshi.Application.Identity;
using Quesshi.Application.Jobs;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Infrastructure.Otp;
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
        Assert.False(await deletion.DeleteAsync(seeded.Account));

        Assert.Null(await ScalarAsync<string?>("select email::text from accounts where id = @account", ("account", seeded.Account)));
        Assert.True(await ScalarAsync<bool>("select deleted_at is not null from accounts where id = @account", ("account", seeded.Account)));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from auth_sessions where account_id = @account", ("account", seeded.Account)));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from account_identities where account_id = @account", ("account", seeded.Account)));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from login_codes where email = @email", ("email", seeded.Email)));
        Assert.Equal(2L, await ScalarAsync<long>("select count(*) from jobs where kind = 'player-cleanup' and owner = 'core' and max_attempts is null"));
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
    public async Task A_failed_cleanup_job_insert_rolls_back_identity_removal_and_does_not_notify()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = Sources();
        var sessions = new PlayerSessionService(sources, new TenantDb(sources), new TestClock(DateTimeOffset.UnixEpoch));
        await sessions.IssueAccountAsync(new DefaultHttpContext(), seeded.FirstTenant, seeded.Account);
        var notifier = new RecordingNotifier();
        var deletion = new AccountDeletionService(new FailingJobInsertSystemDb(new SystemDb(sources)), notifier);

        await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.DeleteAsync(seeded.Account));

        Assert.Equal(seeded.Email, await ScalarAsync<string>("select email::text from accounts where id = @account", ("account", seeded.Account)));
        Assert.False(await ScalarAsync<bool>("select deleted_at is not null from accounts where id = @account", ("account", seeded.Account)));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from auth_sessions where account_id = @account", ("account", seeded.Account)));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from account_identities where account_id = @account", ("account", seeded.Account)));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from login_codes where email = @email", ("email", seeded.Email)));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from jobs where owner = 'core' and kind = 'player-cleanup'"));
        Assert.Empty(notifier.Players);
    }

    [SkippableFact]
    public async Task Account_session_issuance_racing_deletion_cannot_leave_a_live_session()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = Sources();
        var sessions = new PlayerSessionService(sources, new TenantDb(sources), new TestClock(DateTimeOffset.UnixEpoch));
        var deletion = new AccountDeletionService(new SystemDb(sources), new RecordingNotifier());
        await using var owner = await OpenAsync();
        await using var accountLock = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, "select id from accounts where id = @account for update", accountLock, ("account", seeded.Account));

        var issuance = sessions.IssueAccountAsync(new DefaultHttpContext(), seeded.FirstTenant, seeded.Account);
        var deleting = deletion.DeleteAsync(seeded.Account);
        await Task.Delay(100);
        Assert.False(issuance.IsCompleted);
        Assert.False(deleting.IsCompleted);
        await accountLock.CommitAsync();

        try { await issuance; }
        catch (InvalidOperationException) { }
        Assert.True(await deleting);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from auth_sessions where account_id = @account", ("account", seeded.Account)));
        Assert.True(await ScalarAsync<bool>("select deleted_at is not null from accounts where id = @account", ("account", seeded.Account)));
    }

    [SkippableFact]
    public async Task A_deleted_email_can_create_a_new_account_without_inheriting_old_profiles()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = Sources();
        var deletion = new AccountDeletionService(new SystemDb(sources), new RecordingNotifier());
        Assert.True(await deletion.DeleteAsync(seeded.Account));
        var sender = new CapturingSender();
        var emailAuth = new EmailAuthService(sources, new TenantDb(sources), Encoding.UTF8.GetBytes("account-deletion-test-secret"),
            new EmailAuthOptions(5, 20, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)), sender);

        Assert.True(await emailAuth.RequestCodeAsync(seeded.Email, "192.0.2.41", "en"));
        var replacement = Assert.IsType<Guid>(await emailAuth.VerifyCodeAsync(seeded.Email, sender.LastCode));
        var profile = await emailAuth.ConfirmProfileAsync(seeded.FirstTenant, replacement, "Replacement", "replacement", "en");

        Assert.Equal(ProfileConfirmationState.Created, profile.State);
        Assert.NotEqual(seeded.FirstPlayer, profile.PlayerId);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from account_players(@account)", ("account", replacement)));
        Assert.Equal(0L, await TenantScalarAsync<long>(seeded.SecondTenant, "select count(*) from players where account_id = @account", new { account = replacement }));
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
        var cleanup = new CorePlayerCleanup(new TenantLocks(), effects);
        registry.Jobs.Register("core", "player-cleanup", cleanup.HandleAsync);
        registry.Jobs.Register("core", "cleanup-player", effects.HandleCleanupJobAsync);
        var worker = new JobWorker(new PostgresJobStore(sources), registry.Jobs, new TenantDb(sources), new OffCapabilities());

        await worker.RunPassAsync(2, TimeSpan.FromMinutes(1));

        Assert.Equal(CorePlayerCleanup.DeletedDisplayName, await TenantScalarAsync<string>(seeded.FirstTenant, "select display_name from players where id = @player", new { player = seeded.FirstPlayer }));
        Assert.Equal(CorePlayerCleanup.DeletedAvatarSeed, await TenantScalarAsync<string>(seeded.FirstTenant, "select avatar_seed from players where id = @player", new { player = seeded.FirstPlayer }));
        Assert.True(await TenantScalarAsync<bool>(seeded.FirstTenant, "select deleted_at is not null from players where id = @player", new { player = seeded.FirstPlayer }));
        Assert.Equal("Other", await TenantScalarAsync<string>(seeded.FirstTenant, "select display_name from players where id = @player", new { player = seeded.OtherPlayer }));
        Assert.False(await TenantScalarAsync<bool>(seeded.FirstTenant, "select deleted_at is not null from players where id = @player", new { player = seeded.OtherPlayer }));
        Assert.Equal(1L, await TenantScalarAsync<long>(seeded.FirstTenant, "select count(*) from session_participants where player_id = @player", new { player = seeded.FirstPlayer }));
        Assert.Equal(CorePlayerCleanup.DeletedDisplayName, await TenantScalarAsync<string>(seeded.FirstTenant, """
            select p.display_name from session_participants sp
            join players p on p.id = sp.player_id
            where sp.player_id = @player
            """, new { player = seeded.FirstPlayer }));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from jobs where tenant_id = @tenant and kind = 'cleanup-player' and status = 'ready'", ("tenant", seeded.FirstTenant)));

        await cleanup.HandleAsync(new JobContext(0, seeded.FirstTenant, "core", "player-cleanup",
            JsonSerializer.SerializeToElement(new PlayerCleanupPayload(seeded.FirstPlayer)), new TenantDb(sources)), CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from jobs where tenant_id = @tenant and kind = 'cleanup-player'", ("tenant", seeded.FirstTenant)));

        await worker.RunPassAsync(2, TimeSpan.FromMinutes(1));
        Assert.Equal(1L, await TenantScalarAsync<long>(seeded.FirstTenant, "select count(*) from plugin_effects where plugin = @plugin and event_key = @key",
            new { plugin = "cleanup:installed-but-disabled", key = $"player:{seeded.FirstPlayer}" }));
    }

    [SkippableFact]
    public async Task A_failed_core_cleanup_is_retried_without_duplicate_plugin_cleanup()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = Sources();
        await new AccountDeletionService(new SystemDb(sources), new RecordingNotifier()).DeleteAsync(seeded.Account);
        var feature = new CleanupFeature("retry-plugin", retired: false);
        var registry = FeatureRegistry.Create([feature]);
        var effects = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), new NullSessionEffectsNotifier());
        var cleanup = new CorePlayerCleanup(new TenantLocks(), effects);
        var firstAttempt = true;
        registry.Jobs.Register("core", "player-cleanup", async (context, cancellationToken) =>
        {
            if (context.TenantId == seeded.FirstTenant && firstAttempt)
            {
                firstAttempt = false;
                throw new InvalidOperationException("Injected worker interruption.");
            }
            await cleanup.HandleAsync(context, cancellationToken);
        });
        registry.Jobs.Register("core", "cleanup-player", effects.HandleCleanupJobAsync);
        var worker = new JobWorker(new PostgresJobStore(sources), registry.Jobs, new TenantDb(sources), new OffCapabilities());

        await worker.RunPassAsync(2, TimeSpan.FromMinutes(1));
        Assert.Equal(1, await ScalarAsync<int>("select attempts from jobs where tenant_id = @tenant and kind = 'player-cleanup'", ("tenant", seeded.FirstTenant)));
        Assert.Equal("ready", await ScalarAsync<string>("select status from jobs where tenant_id = @tenant and kind = 'player-cleanup'", ("tenant", seeded.FirstTenant)));
        await using (var connection = await OpenAsync("quesshi_system"))
            await ExecuteAsync(connection, "update jobs set run_at = now() where tenant_id = @tenant and kind = 'player-cleanup'", ("tenant", seeded.FirstTenant));

        // The successful tenant can have queued its plugin cleanup first. Drain both
        // ready jobs so this pass reaches the retried core cleanup deterministically.
        await worker.RunPassAsync(2, TimeSpan.FromMinutes(1));
        Assert.True(await TenantScalarAsync<bool>(seeded.FirstTenant, "select deleted_at is not null from players where id = @player", new { player = seeded.FirstPlayer }));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from jobs where tenant_id = @tenant and kind = 'cleanup-player'", ("tenant", seeded.FirstTenant)));
        await cleanup.HandleAsync(new JobContext(0, seeded.FirstTenant, "core", "player-cleanup",
            JsonSerializer.SerializeToElement(new PlayerCleanupPayload(seeded.FirstPlayer)), new TenantDb(sources)), CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from jobs where tenant_id = @tenant and kind = 'cleanup-player'", ("tenant", seeded.FirstTenant)));
    }

    [SkippableFact]
    public async Task Guest_purge_uses_disabled_plugin_cleanup_before_removing_an_abandoned_guest()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        var guest = Guid.NewGuid();
        await using (var connection = await OpenAsync())
        {
            await ExecuteAsync(connection, """
                insert into players (tenant_id, id, display_name, avatar_seed, lang, created_at)
                values (@tenant, @guest, 'Guest', 'guest', 'en', now() - interval '31 days')
                """, ("tenant", seeded.FirstTenant), ("guest", guest));
        }

        await using var sources = Sources();
        var feature = new CleanupFeature("guest-cleanup", retired: true);
        var registry = FeatureRegistry.Create([feature]);
        var effects = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), new NullSessionEffectsNotifier());
        var coreCleanup = new CorePlayerCleanup(new TenantLocks(), effects);
        var removal = new GuestPlayerRemoval(new TenantLocks(), new SystemDb(sources));
        registry.Jobs.Register("core", "cleanup-player", effects.HandleCleanupJobAsync);
        registry.Jobs.Register("core", "guest-remove-player", removal.HandleAsync);
        var purge = new GuestPurge(new TenantLocks(), coreCleanup, new TenantJobs(), new SystemDb(sources), new TestClock(DateTimeOffset.UtcNow));

        await purge.HandleAsync(new JobContext(0, seeded.FirstTenant, "core", "guest-purge",
            JsonSerializer.SerializeToElement(new { }), new TenantDb(sources)), CancellationToken.None);
        Assert.True(await TenantScalarAsync<bool>(seeded.FirstTenant,
            "select deleted_at is not null from players where id = @guest", new { guest }));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from jobs where tenant_id = @tenant and kind = 'cleanup-player'",
            ("tenant", seeded.FirstTenant)));

        var worker = new JobWorker(new PostgresJobStore(sources), registry.Jobs, new TenantDb(sources), new OffCapabilities());
        await worker.RunPassAsync(1, TimeSpan.FromMinutes(1));
        Assert.True(await TenantScalarAsync<bool>(seeded.FirstTenant,
            "select exists(select 1 from players where id = @guest)", new { guest }));
        await worker.RunPassAsync(1, TimeSpan.FromMinutes(1));
        await worker.RunPassAsync(1, TimeSpan.FromMinutes(1));

        Assert.False(await TenantScalarAsync<bool>(seeded.FirstTenant,
            "select exists(select 1 from players where id = @guest)", new { guest }));
        Assert.Equal(1L, await TenantScalarAsync<long>(seeded.FirstTenant,
            "select count(*) from plugin_effects where plugin = @plugin and event_key = @key",
            new { plugin = "cleanup:guest-cleanup", key = $"player:{guest}" }));
    }

    [SkippableFact]
    public async Task Guest_purge_only_scrubs_guests_past_the_exact_cutoff_without_current_activity()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        var now = new DateTimeOffset(2040, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var retainedHistory = Guid.NewGuid();
        var atCutoff = Guid.NewGuid();
        var younger = Guid.NewGuid();
        var authenticated = Guid.NewGuid();
        var seated = Guid.NewGuid();
        await using (var connection = await OpenAsync())
        {
            await ExecuteAsync(connection, """
                insert into players (tenant_id, id, display_name, avatar_seed, lang, created_at)
                values
                  (@tenant, @retainedHistory, 'History', 'history', 'en', @old),
                  (@tenant, @atCutoff, 'Cutoff', 'cutoff', 'en', @cutoff),
                  (@tenant, @younger, 'Young', 'young', 'en', @young),
                  (@tenant, @authenticated, 'Auth', 'auth', 'en', @old),
                  (@tenant, @seated, 'Seated', 'seated', 'en', @old);
                insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, phase, started_at, ended_at)
                values (@tenant, @finishedSession, 'purgedone', 'test', @owner, 'en', 'finished', 2, '{}'::jsonb, 'finished', now(), now());
                insert into session_participants (tenant_id, session_id, player_id, seat)
                values (@tenant, @finishedSession, @retainedHistory, 0);
                insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, phase)
                values (@tenant, @activeSession, 'purgeopen', 'test', @owner, 'en', 'lobby', 2, '{}'::jsonb, 'lobby');
                insert into session_participants (tenant_id, session_id, player_id, seat)
                values (@tenant, @activeSession, @seated, 0);
                insert into auth_sessions (token_hash, tenant_id, guest_id, expires_at)
                values (decode('d1', 'hex'), @tenant, @authenticated, now() + interval '1 day');
                """,
                ("tenant", seeded.FirstTenant), ("retainedHistory", retainedHistory), ("atCutoff", atCutoff), ("younger", younger),
                ("authenticated", authenticated), ("seated", seated), ("old", now - TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1)),
                ("cutoff", now - TimeSpan.FromDays(30)), ("young", now - TimeSpan.FromDays(29)),
                ("finishedSession", Guid.NewGuid()), ("activeSession", Guid.NewGuid()), ("owner", seeded.OtherPlayer));
        }

        await using var sources = Sources();
        var registry = FeatureRegistry.Create([]);
        var effects = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), new NullSessionEffectsNotifier());
        var purge = new GuestPurge(new TenantLocks(), new CorePlayerCleanup(new TenantLocks(), effects), new TenantJobs(), new SystemDb(sources), new TestClock(now));
        await purge.HandleAsync(new JobContext(0, seeded.FirstTenant, "core", "guest-purge",
            JsonSerializer.SerializeToElement(new { }), new TenantDb(sources)), CancellationToken.None);

        Assert.Equal(CorePlayerCleanup.DeletedDisplayName, await TenantScalarAsync<string>(seeded.FirstTenant,
            "select display_name from players where id = @player", new { player = retainedHistory }));
        Assert.True(await TenantScalarAsync<bool>(seeded.FirstTenant,
            "select deleted_at is not null from players where id = @player", new { player = retainedHistory }));
        foreach (var player in new[] { atCutoff, younger, authenticated, seated })
        {
            Assert.False(await TenantScalarAsync<bool>(seeded.FirstTenant,
                "select deleted_at is not null from players where id = @player", new { player }));
        }
    }

    [SkippableFact]
    public async Task Guest_purge_rechecks_sessions_and_unfinished_seats_after_waiting_for_guest_activity()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        var authenticated = Guid.NewGuid();
        var seated = Guid.NewGuid();
        var activeSession = Guid.NewGuid();
        await using (var connection = await OpenAsync())
        {
            await ExecuteAsync(connection, """
                insert into players (tenant_id, id, display_name, avatar_seed, lang, created_at)
                values (@tenant, @authenticated, 'Auth', 'auth', 'en', @createdAt),
                       (@tenant, @seated, 'Seat', 'seat', 'en', @createdAt);
                insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, phase)
                values (@tenant, @session, 'purgerace', 'test', @owner, 'en', 'lobby', 2, '{}'::jsonb, 'lobby');
                """, ("tenant", seeded.FirstTenant), ("authenticated", authenticated), ("seated", seated),
                ("createdAt", now - TimeSpan.FromDays(31)), ("session", activeSession), ("owner", seeded.OtherPlayer));
        }

        await using var sources = Sources();
        var registry = FeatureRegistry.Create([]);
        var effects = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), new NullSessionEffectsNotifier());
        var purge = new GuestPurge(new TenantLocks(), new CorePlayerCleanup(new TenantLocks(), effects), new TenantJobs(), new SystemDb(sources), new TestClock(now));
        await using var blocker = await OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await ExecuteAsync(blocker, "select id from players where id in (@authenticated, @seated) for key share", transaction,
            ("authenticated", authenticated), ("seated", seated));

        var running = purge.HandleAsync(new JobContext(0, seeded.FirstTenant, "core", "guest-purge",
            JsonSerializer.SerializeToElement(new { }), new TenantDb(sources)), CancellationToken.None);
        await Task.Delay(100);
        Assert.False(running.IsCompleted);
        await ExecuteAsync(blocker, """
            insert into auth_sessions (token_hash, tenant_id, guest_id, expires_at)
            values (decode('d2', 'hex'), @tenant, @authenticated, now() + interval '1 day');
            insert into session_participants (tenant_id, session_id, player_id, seat)
            values (@tenant, @session, @seated, 0);
            """, transaction, ("tenant", seeded.FirstTenant), ("authenticated", authenticated), ("session", activeSession), ("seated", seated));
        await transaction.CommitAsync();
        await running;

        foreach (var player in new[] { authenticated, seated })
        {
            Assert.False(await TenantScalarAsync<bool>(seeded.FirstTenant,
                "select deleted_at is not null from players where id = @player", new { player }));
        }
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
        await ExecuteAsync(connection, "insert into tenant_languages (tenant_id, code, name, english_name) values (@first, 'en', 'English', 'English'), (@second, 'en', 'English', 'English')",
            ("first", firstTenant), ("second", secondTenant));
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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction transaction, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
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

    private sealed class CapturingSender : IEmailCodeSender
    {
        public ConcurrentQueue<string> Codes { get; } = [];
        public string LastCode => Codes.Last();
        public Task SendAsync(string email, string code, string language, CancellationToken cancellationToken = default)
        {
            Codes.Enqueue(code);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingJobInsertSystemDb(ISystemDb inner) : ISystemDb
    {
        public Task<T> InTransactionAsync<T>(Func<ISystemTx, Task<T>> callback, CancellationToken cancellationToken = default) =>
            inner.InTransactionAsync(transaction => callback(new FailingJobInsertTransaction(transaction)), cancellationToken);
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) => inner.QueryAsync<T>(sql, parameters, cancellationToken);
        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) => inner.ExecuteAsync(sql, parameters, cancellationToken);
        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) => inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);
    }

    private sealed class FailingJobInsertTransaction(ISystemTx inner) : ISystemTx
    {
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) => inner.QueryAsync<T>(sql, parameters, cancellationToken);
        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            sql.Contains("insert into jobs", StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException("Injected job insert failure.")
                : inner.ExecuteAsync(sql, parameters, cancellationToken);
        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) => inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);
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
