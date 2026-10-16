using System.Collections.Concurrent;
using System.Text;
using Npgsql;
using Quesshi.Application.Identity;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Identity;
using Quesshi.Infrastructure.Otp;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class EmailAuthDatabaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("5c17c8d3-a33c-4a82-a10f-b216262a0449");
    private static readonly Guid TenantB = Guid.Parse("1d0fa565-2d0c-4949-a4b5-1370d5d78e45");
    private DatabaseSources? serviceSources;

    [SkippableFact]
    public async Task Codes_are_hashed_rate_limited_and_consumed_once()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sender = new CapturingSender();
        var service = CreateService(sender, emailLimit: 1, addressLimit: 1);

        Assert.False(await service.RequestCodeAsync("not-an-address", "192.0.2.1", "en"));
        Assert.Empty(sender.Codes);

        const string email = " Person@Example.test ";
        Assert.True(await service.RequestCodeAsync(email, "192.0.2.1", "en"));
        var code = sender.LastCode;
        Assert.True(await service.RequestCodeAsync(email, "192.0.2.1", "en"));
        Assert.Single(sender.Codes);
        Assert.True(await service.RequestCodeAsync("other@example.test", "192.0.2.1", "en"));
        Assert.Single(sender.Codes);

        var hash = await ScalarAsync<byte[]>(owner, "select code_hash from login_codes where email = 'person@example.test'");
        Assert.Equal(32, hash.Length);
        Assert.NotEqual(Encoding.UTF8.GetBytes(code), hash);

        var verifications = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => service.VerifyCodeAsync(email, code)));
        Assert.Single(verifications, account => account is not null);

        const string exhaustedEmail = "attempts@example.test";
        Assert.True(await service.RequestCodeAsync(exhaustedEmail, "192.0.2.2", "en"));
        var wrongCode = sender.LastCode == "000000" ? "000001" : "000000";
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Null(await service.VerifyCodeAsync(exhaustedEmail, wrongCode));
        Assert.Null(await service.VerifyCodeAsync(exhaustedEmail, sender.LastCode));
        Assert.True(await ScalarAsync<bool>(owner, "select expires_at <= now() from login_codes where email = 'attempts@example.test'"));
    }

    [SkippableFact]
    public async Task Profile_tokens_are_tenant_bound_single_use_and_cannot_claim_deleted_accounts()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sender = new CapturingSender();
        var service = CreateService(sender);

        var account = await VerifyNewAccountAsync(service, sender, "profile@example.test", "192.0.2.10");
        var tokenA = service.CreateProfileToken(TenantA, account);
        Assert.Null(await service.ConsumeProfileTokenAsync(tokenA, TenantB));
        Assert.False(await service.IsProfileValidAsync(TenantA, "x", "seed-a", "en"));
        Assert.Equal(account, await service.ConsumeProfileTokenAsync(tokenA, TenantA));
        var firstProfile = await service.ConfirmProfileAsync(TenantA, account, "First profile", "seed-a", "en");
        Assert.Equal(ProfileConfirmationState.Created, firstProfile.State);
        Assert.Null(await service.ConsumeProfileTokenAsync(tokenA, TenantA));
        Assert.Equal(TenantPlayerState.Live, (await service.GetTenantPlayerAsync(TenantA, account)).State);
        Assert.Equal(TenantPlayerState.Missing, (await service.GetTenantPlayerAsync(TenantB, account)).State);

        var tokenB = service.CreateProfileToken(TenantB, account);
        Assert.Equal(account, await service.ConsumeProfileTokenAsync(tokenB, TenantB));
        var secondProfile = await service.ConfirmProfileAsync(TenantB, account, "Second profile", "seed-b", "en");
        Assert.Equal(ProfileConfirmationState.Created, secondProfile.State);
        Assert.NotEqual(firstProfile.PlayerId, secondProfile.PlayerId);

        var deletedAccount = await VerifyNewAccountAsync(service, sender, "deleted@example.test", "192.0.2.11");
        var deletedToken = service.CreateProfileToken(TenantA, deletedAccount);
        await ExecuteAsync(owner, "update accounts set email = null, deleted_at = now() where id = @account", ("account", deletedAccount));
        Assert.Equal(deletedAccount, await service.ConsumeProfileTokenAsync(deletedToken, TenantA));
        Assert.Equal(ProfileConfirmationState.Unavailable,
            (await service.ConfirmProfileAsync(TenantA, deletedAccount, "Deleted profile", "seed", "en")).State);
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from players where account_id = @account", ("account", deletedAccount)));
    }

    [SkippableFact]
    public async Task Concurrent_profile_confirmations_create_one_player_and_both_succeed()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sender = new CapturingSender();
        var service = CreateService(sender);
        var account = await VerifyNewAccountAsync(service, sender, "concurrent-profile@example.test", "192.0.2.12");
        var firstToken = service.CreateProfileToken(TenantA, account);
        var secondToken = service.CreateProfileToken(TenantA, account);
        Assert.Equal(account, await service.ConsumeProfileTokenAsync(firstToken, TenantA));
        Assert.Equal(account, await service.ConsumeProfileTokenAsync(secondToken, TenantA));

        await ExecuteAsync(owner, """
            create function pause_profile_creation() returns trigger language plpgsql as $$
            begin
              perform pg_sleep(0.2);
              return new;
            end
            $$;
            create trigger pause_profile_creation before insert on players
            for each row execute function pause_profile_creation();
            """);
        try
        {
            var results = await Task.WhenAll(
                service.ConfirmProfileAsync(TenantA, account, "Concurrent profile", "seed-a", "en"),
                service.ConfirmProfileAsync(TenantA, account, "Concurrent profile", "seed-b", "en"));

            Assert.Contains(results, result => result.State == ProfileConfirmationState.Created);
            Assert.Contains(results, result => result.State == ProfileConfirmationState.Existing);
            Assert.All(results, result => Assert.NotNull(result.PlayerId));
            Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from players where tenant_id = @tenant and account_id = @account", ("tenant", TenantA), ("account", account)));
        }
        finally
        {
            await ExecuteAsync(owner, "drop trigger if exists pause_profile_creation on players; drop function if exists pause_profile_creation()");
        }
    }

    [SkippableFact]
    public async Task Expired_rate_limits_and_profile_token_records_are_removed_during_authentication()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sender = new CapturingSender();
        var service = CreateService(sender);
        await ExecuteAsync(owner, """
            insert into auth_rate_limits (key, window_start, count)
            values ('code:expired@example.test', now() - interval '11 minutes', 1),
                   ('profile:expired', now() - interval '1 minute', 1)
            """);

        Assert.True(await service.RequestCodeAsync("cleanup@example.test", "192.0.2.20", "en"));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from auth_rate_limits where key in ('code:expired@example.test', 'profile:expired')"));
    }

    [SkippableFact]
    public async Task Emailed_code_account_returns_profile_needed_through_external_completion_without_a_player()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sender = new CapturingSender();
        var emailAuth = CreateService(sender);
        var account = await VerifyNewAccountAsync(emailAuth, sender, "code@example.test", "192.0.2.30");
        await using var externalSources = CreateSources();
        var external = CreateExternalService(externalSources);

        var completion = await external.CompleteExternalAsync(new ExternalIdentityTenant(TenantA, "en"), "google", "email-code-subject", "code@example.test", true);

        Assert.Equal(ExternalIdentityCompletionState.ProfileNeeded, completion.State);
        Assert.Equal(account, completion.AccountId);
        Assert.NotNull(completion.Profile);
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from players where account_id = @account", ("account", account)));
    }

    [SkippableFact]
    public async Task External_completion_returns_signed_in_for_a_live_player()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sender = new CapturingSender();
        var emailAuth = CreateService(sender);
        var account = await VerifyNewAccountAsync(emailAuth, sender, "live@example.test", "192.0.2.31");
        await emailAuth.ConfirmProfileAsync(TenantA, account, "Live player", "live-seed", "en");
        await using var externalSources = CreateSources();

        var completion = await CreateExternalService(externalSources).CompleteExternalAsync(
            new ExternalIdentityTenant(TenantA, "en"), "google", "live-player-subject", "live@example.test", true);

        Assert.Equal(ExternalIdentityCompletionState.SignedIn, completion.State);
        Assert.Equal(account, completion.AccountId);
        Assert.Null(completion.Profile);
    }

    [SkippableFact]
    public async Task External_completion_refuses_an_unavailable_tenant_player()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var sender = new CapturingSender();
        var emailAuth = CreateService(sender);
        var account = await VerifyNewAccountAsync(emailAuth, sender, "banned@example.test", "192.0.2.32");
        await emailAuth.ConfirmProfileAsync(TenantA, account, "Banned player", "banned-seed", "en");
        await ExecuteAsync(owner, "update players set banned_at = now() where tenant_id = @tenant and account_id = @account", ("tenant", TenantA), ("account", account));
        await using var externalSources = CreateSources();

        var completion = await CreateExternalService(externalSources).CompleteExternalAsync(
            new ExternalIdentityTenant(TenantA, "en"), "google", "banned-player-subject", "banned@example.test", true);

        Assert.Equal(ExternalIdentityCompletionState.Unavailable, completion.State);
        Assert.Null(completion.AccountId);
    }

    private EmailAuthService CreateService(CapturingSender sender, int emailLimit = 5, int addressLimit = 20) => new(
        ServiceSources,
        new TenantDb(ServiceSources),
        Encoding.UTF8.GetBytes("email-auth-test-secret"),
        new EmailAuthOptions(emailLimit, addressLimit, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)),
        sender);

    private DatabaseSources ServiceSources => serviceSources ??= new DatabaseSources(
        fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => serviceSources?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    private DatabaseSources CreateSources() => new(
        fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static ExternalIdentityService CreateExternalService(DatabaseSources sources) => new(
        sources, new TenantDb(sources), Encoding.UTF8.GetBytes("email-auth-test-secret"), TimeSpan.FromMinutes(10));

    private static async Task<Guid> VerifyNewAccountAsync(EmailAuthService service, CapturingSender sender, string email, string address)
    {
        Assert.True(await service.RequestCodeAsync(email, address, "en"));
        return Assert.IsType<Guid>(await service.VerifyCodeAsync(email, sender.LastCode));
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await ExecuteAsync(owner, "delete from auth_sessions; delete from account_identities; delete from login_codes; delete from auth_rate_limits; delete from rate_limits; delete from data_protection_keys; delete from session_participants; delete from sessions; delete from tenants; delete from accounts");
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'email-auth-a', 'Email auth A', 'en'), (@b, 'email-auth-b', 'Email auth B', 'en')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@a, 'en', 'English', 'English'), (@b, 'en', 'English', 'English')", ("a", TenantA), ("b", TenantB));
        return owner;
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
}
