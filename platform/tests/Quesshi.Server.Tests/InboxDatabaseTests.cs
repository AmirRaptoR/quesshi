using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class InboxDatabaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Application_role_persists_a_tenant_scoped_session_invite()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync(includeSession: true);
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var expiresAt = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await database.InTenantAsync(seeded.TenantId, transaction => transaction.ExecuteAsync(
            "insert into session_invites (tenant_id, session_id, player_id, invited_by, expires_at) values (current_setting('app.tenant_id')::uuid, @sessionId, @playerId, @invitedBy, @expiresAt)",
            new { sessionId = seeded.SessionId, playerId = seeded.SecondPlayerId, invitedBy = seeded.FirstPlayerId, expiresAt }));

        var persisted = await database.InTenantAsync(seeded.TenantId, transaction => transaction.QuerySingleOrDefaultAsync<DateTimeOffset?>(
            "select expires_at from session_invites where tenant_id = current_setting('app.tenant_id')::uuid and session_id = @sessionId and player_id = @playerId",
            new { sessionId = seeded.SessionId, playerId = seeded.SecondPlayerId }));

        Assert.Equal(expiresAt, persisted);
    }

    [SkippableFact]
    public async Task Application_role_persists_a_tenant_scoped_player_inbox_version()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);

        await database.InTenantAsync(seeded.TenantId, transaction => transaction.ExecuteAsync(
            "insert into player_inbox (tenant_id, player_id, version) values (current_setting('app.tenant_id')::uuid, @playerId, 7)",
            new { playerId = seeded.FirstPlayerId }));

        var persisted = await database.InTenantAsync(seeded.TenantId, transaction => transaction.QuerySingleOrDefaultAsync<long?>(
            "select version from player_inbox where tenant_id = current_setting('app.tenant_id')::uuid and player_id = @playerId",
            new { playerId = seeded.FirstPlayerId }));

        Assert.Equal(7L, persisted);
    }

    [SkippableFact]
    public async Task First_committed_inbox_change_for_an_existing_player_creates_version_one()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        IInbox inbox = new TenantInbox();

        await database.InTenantAsync(seeded.TenantId, transaction =>
        {
            inbox.Changed(transaction, seeded.FirstPlayerId);
            return Task.FromResult(0);
        });

        var persisted = await VersionAsync(database, seeded.TenantId, seeded.FirstPlayerId);

        Assert.Equal(1L, persisted);
    }

    [SkippableFact]
    public async Task Repeated_inbox_changes_in_one_transaction_increment_a_player_once()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        IInbox inbox = new TenantInbox();
        await InsertVersionAsync(database, seeded.TenantId, seeded.FirstPlayerId, 4);

        await database.InTenantAsync(seeded.TenantId, transaction =>
        {
            inbox.Changed(transaction, seeded.FirstPlayerId);
            inbox.Changed(transaction, seeded.FirstPlayerId);
            inbox.Changed(transaction, seeded.FirstPlayerId);
            return Task.FromResult(0);
        });

        var persisted = await VersionAsync(database, seeded.TenantId, seeded.FirstPlayerId);

        Assert.Equal(5L, persisted);
    }

    [SkippableFact]
    public async Task Committed_inbox_change_leaves_an_unaffected_player_version_unchanged()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        IInbox inbox = new TenantInbox();
        await InsertVersionAsync(database, seeded.TenantId, seeded.SecondPlayerId, 4);

        await database.InTenantAsync(seeded.TenantId, transaction =>
        {
            inbox.Changed(transaction, seeded.FirstPlayerId);
            return Task.FromResult(0);
        });

        var persisted = await VersionAsync(database, seeded.TenantId, seeded.SecondPlayerId);

        Assert.Equal(4L, persisted);
    }

    [SkippableFact]
    public async Task Inbox_version_writes_follow_other_writes_in_ascending_player_order()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await CreateInboxWriteAuditAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        IInbox inbox = new TenantInbox();
        var playerIds = new[] { seeded.FirstPlayerId, seeded.SecondPlayerId };
        Array.Sort(playerIds);

        await database.InTenantAsync(seeded.TenantId, transaction =>
        {
            return WriteAndChangeInboxesAsync(transaction, inbox, seeded.FirstPlayerId, seeded.SecondPlayerId);
        });

        var writes = await database.InTenantAsync(seeded.TenantId, transaction => transaction.QueryAsync<InboxWrite>(
            "select sequence, player_id as PlayerId, kind from inbox_write_log where tenant_id = current_setting('app.tenant_id')::uuid order by sequence"));

        Assert.Equal(
            new[] { new InboxWrite(1, null, "before"), new InboxWrite(2, playerIds[0], "inbox"), new InboxWrite(3, playerIds[1], "inbox") },
            writes);
    }

    [SkippableFact]
    public async Task Committed_inbox_change_emits_a_durable_inbox_notification()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = Sources();
        var database = new TenantDb(sources);
        IInbox inbox = new TenantInbox();
        var listener = new PostgresStreamNotificationListener(sources, NullLogger<PostgresStreamNotificationListener>.Instance);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<StreamNotification.Inbox>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Received += notification =>
        {
            if (notification is StreamNotification.Reconnected) reconnected.TrySetResult();
            if (notification is StreamNotification.Inbox inboxNotification) received.TrySetResult(inboxNotification);
        };

        await listener.StartAsync(CancellationToken.None);
        try
        {
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await database.InTenantAsync(seeded.TenantId, transaction =>
            {
                inbox.Changed(transaction, seeded.FirstPlayerId);
                return Task.FromResult(0);
            });

            var notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal((seeded.TenantId, seeded.FirstPlayerId, 1L), (notification.TenantId, notification.PlayerId, notification.Version));
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static Task<long?> VersionAsync(TenantDb database, Guid tenantId, Guid playerId) =>
        database.InTenantAsync(tenantId, transaction => transaction.QuerySingleOrDefaultAsync<long?>(
            "select version from player_inbox where tenant_id = current_setting('app.tenant_id')::uuid and player_id = @playerId",
            new { playerId }));

    private static Task<int> InsertVersionAsync(TenantDb database, Guid tenantId, Guid playerId, long version) =>
        database.InTenantAsync(tenantId, transaction => transaction.ExecuteAsync(
            "insert into player_inbox (tenant_id, player_id, version) values (current_setting('app.tenant_id')::uuid, @playerId, @version)",
            new { playerId, version }));

    private static async Task<int> WriteAndChangeInboxesAsync(ITenantTx transaction, IInbox inbox, Guid firstPlayerId, Guid secondPlayerId)
    {
        await transaction.ExecuteAsync(
            "insert into inbox_write_log (tenant_id, player_id, kind) values (current_setting('app.tenant_id')::uuid, null, 'before')");
        inbox.Changed(transaction, secondPlayerId);
        inbox.Changed(transaction, firstPlayerId);
        return 0;
    }

    private async Task CreateInboxWriteAuditAsync()
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync(
            "drop table if exists inbox_write_log; " +
            "drop function if exists record_player_inbox_write(); " +
            "create table inbox_write_log (tenant_id uuid not null, sequence bigint generated always as identity, player_id uuid, kind text not null, primary key (tenant_id, sequence)); " +
            "select quesshi_tenant_table('inbox_write_log', 'core'); " +
            "grant usage, select on sequence inbox_write_log_sequence_seq to quesshi_app; " +
            "create function record_player_inbox_write() returns trigger language plpgsql as $$ begin insert into inbox_write_log (tenant_id, player_id, kind) values (new.tenant_id, new.player_id, 'inbox'); return new; end $$; " +
            "create trigger record_player_inbox_write after insert or update on player_inbox for each row execute function record_player_inbox_write();");
    }

    private async Task<InboxSeed> SeedAsync(bool includeSession = false)
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var seeded = new InboxSeed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), includeSession ? Guid.NewGuid() : null);
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync(
            "insert into tenants (id, slug, name, default_language) values (@tenantId, @slug, 'Inbox tests', 'en'); " +
            "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenantId, @firstPlayerId, 'First', 'first', 'en'), (@tenantId, @secondPlayerId, 'Second', 'second', 'en');",
            new { tenantId = seeded.TenantId, slug = $"inbox-{seeded.TenantId:N}", firstPlayerId = seeded.FirstPlayerId, secondPlayerId = seeded.SecondPlayerId });
        if (seeded.SessionId is Guid sessionId)
        {
            await owner.ExecuteAsync(
                "insert into sessions (tenant_id, id, code, format, owner_id, lang, capacity, content) values (@tenantId, @sessionId, @code, 'async_duel', @ownerId, 'en', 2, '{}'::jsonb)",
                new { tenantId = seeded.TenantId, sessionId, code = $"i{seeded.TenantId:N}"[..12], ownerId = seeded.FirstPlayerId });
        }
        return seeded;
    }

    private sealed record InboxSeed(Guid TenantId, Guid FirstPlayerId, Guid SecondPlayerId, Guid? SessionId);

    private sealed record InboxWrite(long Sequence, Guid? PlayerId, string Kind);
}
