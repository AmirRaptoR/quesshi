using Npgsql;
using Quesshi.Application.Identity;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Identity;
using Quesshi.Infrastructure.Security;
using Quesshi.Server;
using Quesshi.Server.Commands;
using Quesshi.Server.Tenants;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AdminAddDatabaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("e98eb1ef-8259-4aa8-8b24-522589cb9c62");
    private static readonly Guid TenantB = Guid.Parse("1ad7795b-876a-49bc-a631-0d3da6939c32");

    [SkippableFact]
    public async Task Tenant_admin_can_sign_in_only_for_its_member_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();

        await Program.AddAdminAsync(["--tenant", "admin-add-a", "tenant-owner", "tenant-owner@example.test", "A strong password 2026!"], sources);
        var authentication = Authentication(sources);
        var result = await authentication.LoginAsync(TenantB, "tenant-owner", "A strong password 2026!");

        Assert.Equal(AdminLoginStatus.InvalidCredentials, result.Status);
    }

    [SkippableFact]
    public async Task Tenant_admin_is_active_owner_of_its_member_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();

        var adminId = await Program.AddAdminAsync(["--tenant", "admin-add-a", "tenant-owner", "tenant-owner@example.test", "A strong password 2026!"], sources);
        var isActiveOwner = await ScalarAsync<bool>(owner, """
            select a.is_active and exists (
                select 1 from admin_memberships m where m.admin_id = a.id and m.tenant_id = @tenantId and m.role = 'owner'
            ) from admins a where a.id = @adminId
            """, ("adminId", adminId), ("tenantId", TenantA));

        Assert.True(isActiveOwner);
    }

    [SkippableFact]
    public async Task Tenant_admin_password_is_usable_for_its_member_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();

        await Program.AddAdminAsync(["--tenant", "admin-add-a", "tenant-owner", "tenant-owner@example.test", "A strong password 2026!"], sources);
        var result = await Authentication(sources).LoginAsync(TenantA, "tenant-owner", "A strong password 2026!");

        Assert.Equal(AdminLoginStatus.Success, result.Status);
    }

    [SkippableFact]
    public async Task Platform_admin_can_sign_in_for_any_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();

        await Program.AddAdminAsync(["--platform", "platform-owner", "platform-owner@example.test", "Platform password 2026!"], sources);
        var result = await Authentication(sources).LoginAsync(TenantB, "platform-owner", "Platform password 2026!");

        Assert.Equal(AdminLoginStatus.Success, result.Status);
    }

    [SkippableFact]
    public async Task Combined_flags_create_a_platform_owner_membership()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();

        var adminId = await Program.AddAdminAsync(["--tenant", "admin-add-a", "--platform", "combined-owner", "combined-owner@example.test", "Combined password 2026!"], sources);
        var created = await ScalarAsync<bool>(owner, """
            select a.is_platform and exists (
                select 1 from admin_memberships m where m.admin_id = a.id and m.tenant_id = @tenantId and m.role = 'owner'
            ) from admins a where a.id = @adminId
            """, ("adminId", adminId), ("tenantId", TenantA));

        Assert.True(created);
    }

    [SkippableFact]
    public async Task Nonexistent_tenant_does_not_create_an_admin()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();

        await Assert.ThrowsAsync<CommandException>(() => Program.AddAdminAsync(["--tenant", "missing", "missing-owner", "missing@example.test", "Missing password 2026!"], sources));
        var count = await ScalarAsync<long>(owner, "select count(*) from admins");

        Assert.Equal(0L, count);
    }

    [SkippableFact]
    public async Task Duplicate_username_does_not_create_an_admin_or_membership()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();

        await Program.AddAdminAsync(["--tenant", "admin-add-a", "duplicate-owner", "first@example.test", "First password 2026!"], sources);
        await Assert.ThrowsAsync<CommandException>(() => Program.AddAdminAsync(["--tenant", "admin-add-b", "duplicate-owner", "second@example.test", "Second password 2026!"], sources));
        var membershipCount = await ScalarAsync<long>(owner, "select count(*) from admin_memberships");

        Assert.Equal(1L, membershipCount);
    }

    [SkippableFact]
    public async Task Duplicate_email_does_not_create_an_admin_or_membership()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();

        await Program.AddAdminAsync(["--tenant", "admin-add-a", "first-owner", "duplicate@example.test", "First password 2026!"], sources);
        await Assert.ThrowsAsync<CommandException>(() => Program.AddAdminAsync(["--tenant", "admin-add-b", "second-owner", "duplicate@example.test", "Second password 2026!"], sources));
        var adminCount = await ScalarAsync<long>(owner, "select count(*) from admins");

        Assert.Equal(1L, adminCount);
    }

    [SkippableFact]
    public async Task Invalid_email_does_not_create_an_admin()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();

        await Assert.ThrowsAsync<CommandException>(() => Program.AddAdminAsync(["--tenant", "admin-add-a", "invalid-email", "not-an-email", "Valid password 2026!"], sources));
        var count = await ScalarAsync<long>(owner, "select count(*) from admins");

        Assert.Equal(0L, count);
    }

    [SkippableFact]
    public async Task Weak_password_does_not_create_an_admin()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();

        await Assert.ThrowsAsync<CommandException>(() => Program.AddAdminAsync(["--tenant", "admin-add-a", "weak-password", "weak@example.test", "short"], sources));
        var count = await ScalarAsync<long>(owner, "select count(*) from admins");

        Assert.Equal(0L, count);
    }

    [SkippableFact]
    public async Task First_server_start_creates_a_must_change_owner_that_can_sign_in()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync(includeSecondTenant: false);
        await using var sources = Sources();
        using var output = new StringWriter();

        await Program.ServeAsync(sources, static _ => Task.CompletedTask, bootstrapOutput: output);
        var credentials = CredentialsFrom(output.ToString());
        var login = await Authentication(sources).LoginAsync(TenantA, credentials.Username, credentials.Password);

        Assert.True(login is { Status: AdminLoginStatus.Success, MustChangePassword: true });
    }

    [SkippableFact]
    public async Task Restart_does_not_print_another_temporary_password()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync(includeSecondTenant: false);
        await using var sources = Sources();
        using var firstOutput = new StringWriter();
        using var restartOutput = new StringWriter();

        await Program.ServeAsync(sources, static _ => Task.CompletedTask, bootstrapOutput: firstOutput);
        await Program.ServeAsync(sources, static _ => Task.CompletedTask, bootstrapOutput: restartOutput);
        Assert.True(firstOutput.ToString().Contains("password", StringComparison.Ordinal) && restartOutput.ToString().Length == 0);
    }

    [SkippableFact]
    public async Task Restart_does_not_create_another_tenant_admin()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync(includeSecondTenant: false);
        await using var sources = Sources();
        using var firstOutput = new StringWriter();
        using var restartOutput = new StringWriter();

        await Program.ServeAsync(sources, static _ => Task.CompletedTask, bootstrapOutput: firstOutput);
        await Program.ServeAsync(sources, static _ => Task.CompletedTask, bootstrapOutput: restartOutput);
        var adminCount = await ScalarAsync<long>(owner, "select count(*) from admins");

        Assert.Equal(1L, adminCount);
    }

    [SkippableFact]
    public async Task Bootstrap_uses_distinct_global_identities_for_distinct_tenants()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        using var output = new StringWriter();

        await Program.ServeAsync(sources, static _ => Task.CompletedTask, bootstrapOutput: output);
        var uniqueAdminCount = await ScalarAsync<long>(owner, """
            select count(*) from admins
            where (select count(distinct username) from admins) = 2
              and (select count(distinct email) from admins) = 2
            """);

        Assert.Equal(2L, uniqueAdminCount);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (fixture.ConnectionString is null) return;
        await using var owner = await OpenAsync("quesshi_owner");
        await ClearAsync(owner);
    }

    private AdminAuthService Authentication(DatabaseSources sources) => new(sources, new IdentityPasswordHasher(), new NullMailer(), new TestClock(DateTimeOffset.UtcNow));

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private async Task<NpgsqlConnection> PrepareAsync(bool includeSecondTenant = true)
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = await OpenAsync("quesshi_owner");
        await ClearAsync(owner);
        await ExecuteAsync(owner, includeSecondTenant
            ? """
                insert into tenants (id, slug, name, default_language)
                values (@a, 'admin-add-a', 'Admin add A', 'en'), (@b, 'admin-add-b', 'Admin add B', 'en')
                """
            : """
                insert into tenants (id, slug, name, default_language)
                values (@a, 'admin-add-a', 'Admin add A', 'en')
                """, ("a", TenantA), ("b", TenantB));
        return owner;
    }

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static Task ClearAsync(NpgsqlConnection connection) => ExecuteAsync(connection, """
        delete from auth_sessions;
        delete from admin_password_resets;
        delete from admin_memberships;
        delete from admins;
        delete from tenant_languages;
        delete from tenant_hosts;
        delete from tenants;
        """);

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

    private static TenantAdminCredentials CredentialsFrom(string output)
    {
        const string usernameMarker = "username ";
        const string passwordMarker = "; password ";
        const string suffixMarker = ". Change this password";
        var usernameStart = output.IndexOf(usernameMarker, StringComparison.Ordinal);
        var passwordStart = output.IndexOf(passwordMarker, StringComparison.Ordinal);
        var passwordEnd = output.IndexOf(suffixMarker, StringComparison.Ordinal);
        if (usernameStart < 0 || passwordStart < 0 || passwordEnd < 0)
        {
            throw new InvalidOperationException("Bootstrap output did not include temporary credentials.");
        }

        var username = output[(usernameStart + usernameMarker.Length)..passwordStart];
        var password = output[(passwordStart + passwordMarker.Length)..passwordEnd];
        return new TenantAdminCredentials(username, $"{username}@bootstrap.invalid", password);
    }

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }

    private sealed class NullMailer : IAdminPasswordResetMailer
    {
        public Task SendAsync(string email, string username, string resetLink, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
