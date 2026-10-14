using System.Collections.Concurrent;
using System.Text;
using Dapper;
using Npgsql;
using Quesshi.Application.Identity;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Otp;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Shared;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class GuestUpgradeDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Valid_code_attaches_the_existing_guest_and_emits_an_upgrade_event()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sender = new CapturingSender();
        var events = new RecordingEvents();
        var service = CreateService(sender, events);
        var guest = await CreateGuestAsync(owner, TenantA);
        var earlierSession = Guid.NewGuid();
        await owner.ExecuteAsync("""
            insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, phase, started_at, ended_at)
            values (@tenant, @session, 'upgraded', 'test', @guest, 'en', 'finished', 1, '{}'::jsonb, 'finished', now(), now());
            insert into session_participants (tenant_id, session_id, player_id, seat, eligible)
            values (@tenant, @session, @guest, 0, false);
            """, new { tenant = TenantA, session = earlierSession, guest });

        Assert.True(await service.RequestCodeAsync("upgrade@example.test", "192.0.2.1", "en"));
        var result = await service.VerifyAndAttachAsync(TenantA, guest, "upgrade@example.test", sender.LastCode);

        Assert.Equal(GuestUpgradeState.Upgraded, result.State);
        var account = Assert.IsType<Guid>(result.AccountId);
        Assert.Equal(account, await owner.QuerySingleAsync<Guid?>("select account_id from players where tenant_id = @tenant and id = @guest", new { tenant = TenantA, guest }));
        Assert.Equal(guest, Assert.Single(events.Upgrades).PlayerId);
        Assert.Equal(guest, await owner.QuerySingleAsync<Guid>("select player_id from session_participants where tenant_id = @tenant and session_id = @session", new { tenant = TenantA, session = earlierSession }));
        Assert.False(await owner.QuerySingleAsync<bool>("select eligible from session_participants where tenant_id = @tenant and session_id = @session", new { tenant = TenantA, session = earlierSession }));
    }

    [SkippableFact]
    public async Task Run_started_after_upgrade_uses_snapshot_account_eligibility()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sender = new CapturingSender();
        var service = CreateService(sender, new RecordingEvents());
        var guest = await CreateGuestAsync(owner, TenantA);

        Assert.True(await service.RequestCodeAsync("eligible-after-upgrade@example.test", "192.0.2.5", "en"));
        Assert.Equal(GuestUpgradeState.Upgraded,
            (await service.VerifyAndAttachAsync(TenantA, guest, "eligible-after-upgrade@example.test", sender.LastCode)).State);

        var unupgradedGuest = await CreateGuestAsync(owner, TenantA);
        var laterSession = Guid.NewGuid();
        await owner.ExecuteAsync("""
            insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, started_at)
            values (@tenant, @session, @code, 'test', @owner, 'en', 'running', 2, '{}'::jsonb, now());
            insert into session_participants (tenant_id, session_id, player_id, seat)
            values (@tenant, @session, @upgraded, 0), (@tenant, @session, @guest, 1);
            """, new { tenant = TenantA, session = laterSession, code = $"upgrade-{laterSession:N}", owner = guest, upgraded = guest, guest = unupgradedGuest });

        var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        var snapshot = Assert.IsType<SessionState>(await new SessionSnapshotLoader(new TenantDb(sources)).LoadAsync(TenantA, laterSession));
        Assert.False(snapshot.Seats[guest.ToString()].IsGuest);
        Assert.True(snapshot.Seats[unupgradedGuest.ToString()].IsGuest);

        var transition = new SessionTransition(snapshot, new KindRegistry([]), new TestClock(DateTimeOffset.UnixEpoch), TimeSpan.Zero);
        transition.Serve(guest.ToString(), 0);
        transition.Serve(unupgradedGuest.ToString(), 0);

        Assert.True(transition.Current.Seats[guest.ToString()].Eligible);
        Assert.False(transition.Current.Seats[unupgradedGuest.ToString()].Eligible);
    }

    [SkippableFact]
    public async Task Existing_cross_tenant_account_attaches_but_a_tenant_player_is_refused()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sender = new CapturingSender();
        var service = CreateService(sender, new RecordingEvents());
        var account = Guid.NewGuid();
        var guest = await CreateGuestAsync(owner, TenantA);
        await owner.ExecuteAsync("insert into accounts(id, email) values (@account, 'existing@example.test')", new { account });
        await owner.ExecuteAsync("insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang) values (@tenant, @id, @account, 'Other tenant', 'seed', 'en')", new { tenant = TenantB, id = Guid.NewGuid(), account });

        Assert.True(await service.RequestCodeAsync("existing@example.test", "192.0.2.2", "en"));
        var attached = await service.VerifyAndAttachAsync(TenantA, guest, "existing@example.test", sender.LastCode);
        Assert.Equal(GuestUpgradeState.Upgraded, attached.State);
        Assert.Equal(account, attached.AccountId);

        var blockedGuest = await CreateGuestAsync(owner, TenantA);
        await owner.ExecuteAsync("insert into accounts(id, email) values (@account, 'taken@example.test')", new { account = Guid.NewGuid() });
        var taken = await owner.QuerySingleAsync<Guid>("select id from accounts where email = 'taken@example.test'");
        await owner.ExecuteAsync("insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang) values (@tenant, @id, @account, 'Taken player', 'seed', 'en')", new { tenant = TenantA, id = Guid.NewGuid(), account = taken });

        Assert.True(await service.RequestCodeAsync("taken@example.test", "192.0.2.3", "en"));
        var refused = await service.VerifyAndAttachAsync(TenantA, blockedGuest, "taken@example.test", sender.LastCode);
        Assert.Equal(GuestUpgradeState.AddressTaken, refused.State);
        Assert.Null(await owner.QuerySingleAsync<Guid?>("select account_id from players where tenant_id = @tenant and id = @guest", new { tenant = TenantA, guest = blockedGuest }));
    }

    [SkippableFact]
    public async Task Concurrent_claims_only_attach_one_guest_to_an_account()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var service = CreateService(new CapturingSender(), new RecordingEvents());
        var account = Guid.NewGuid();
        var first = await CreateGuestAsync(owner, TenantA);
        var second = await CreateGuestAsync(owner, TenantA);
        await owner.ExecuteAsync("insert into accounts(id, email) values (@account, 'race@example.test')", new { account });

        var results = await Task.WhenAll(
            service.AttachAsync(TenantA, first, account),
            service.AttachAsync(TenantA, second, account));

        Assert.Single(results, result => result.State == GuestUpgradeState.Upgraded);
        Assert.Single(results, result => result.State == GuestUpgradeState.AddressTaken);
        Assert.Equal(1L, await owner.QuerySingleAsync<long>("select count(*) from players where tenant_id = @tenant and account_id = @account", new { tenant = TenantA, account }));
    }

    [SkippableFact]
    public async Task Invalid_and_exhausted_codes_leave_the_guest_unattached()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sender = new CapturingSender();
        var service = CreateService(sender, new RecordingEvents());
        var guest = await CreateGuestAsync(owner, TenantA);
        const string email = "failed-upgrade@example.test";

        Assert.True(await service.RequestCodeAsync(email, "192.0.2.4", "en"));
        var wrong = sender.LastCode == "000000" ? "000001" : "000000";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await service.VerifyAndAttachAsync(TenantA, guest, email, wrong);
            Assert.Equal(GuestUpgradeState.VerificationFailed, result.State);
        }
        Assert.Equal(GuestUpgradeState.VerificationFailed,
            (await service.VerifyAndAttachAsync(TenantA, guest, email, sender.LastCode)).State);
        Assert.Null(await owner.QuerySingleAsync<Guid?>("select account_id from players where tenant_id = @tenant and id = @guest", new { tenant = TenantA, guest }));
        Assert.Equal(0L, await owner.QuerySingleAsync<long>("select count(*) from accounts where email = @email", new { email }));
    }

    private GuestUpgradeService CreateService(CapturingSender sender, RecordingEvents events)
    {
        var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        return new GuestUpgradeService(new EmailAuthService(
            sources,
            new TenantDb(sources),
            Encoding.UTF8.GetBytes("guest-upgrade-test-secret"),
            EmailAuthOptions.Default,
            sender),
            sources,
            new TenantDb(sources),
            events);
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("delete from auth_sessions; delete from account_identities; delete from login_codes; delete from auth_rate_limits; delete from rate_limits; delete from session_participants; delete from sessions; delete from tenants; delete from accounts");
        await owner.ExecuteAsync("insert into tenants (id, slug, name, default_language) values (@a, 'guest-upgrade-a', 'Guest upgrade A', 'en'), (@b, 'guest-upgrade-b', 'Guest upgrade B', 'en')", new { a = TenantA, b = TenantB });
        return owner;
    }

    private static Task<Guid> CreateGuestAsync(NpgsqlConnection owner, Guid tenant)
    {
        var guest = Guid.NewGuid();
        return owner.ExecuteScalarAsync<Guid>("insert into players(tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @guest, 'Guest', 'seed', 'en') returning id", new { tenant, guest });
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

    private sealed class RecordingEvents : IEvents
    {
        public ConcurrentQueue<PlayerUpgraded> Upgrades { get; } = [];
        public Task EmitAsync<TEvent>(ITenantTx transaction, TEvent @event, CancellationToken cancellationToken = default)
        {
            if (@event is PlayerUpgraded upgraded) Upgrades.Enqueue(upgraded);
            return Task.CompletedTask;
        }
    }

    private static readonly Guid TenantA = Guid.Parse("4daaee42-50e3-4f18-aaf6-8622cdb75c82");
    private static readonly Guid TenantB = Guid.Parse("2634140f-0f5c-4d9c-a4c2-b2c10d455aed");
}
