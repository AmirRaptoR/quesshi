using System.Text;
using Npgsql;
using Quesshi.Application.Tenancy;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Identity;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Infrastructure.Tests;

[Collection("PostgreSQL")]
public sealed class ExternalIdentityDatabaseTests(PostgresFixture fixture)
{
    [Fact]
    public void Core_migrations_include_subject_only_identity_accounts()
    {
        var migrations = MigrationRunner.Discover(typeof(ITenants).Assembly);

        var migration = Assert.Single(migrations, migration => migration.Name == "20261015T0301_core_subject_only_external_identities.sql");
        Assert.Contains("drop constraint if exists accounts_check", Encoding.UTF8.GetString(migration.Contents.Span), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drop constraint if exists accounts_email_deleted_check", Encoding.UTF8.GetString(migration.Contents.Span), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Subject_precedes_changed_email_and_concurrent_completion_creates_one_identity()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var service = CreateService();

        var first = await service.CompleteExternalAsync("google", "subject-priority", "first@example.test", true);
        var repeated = await service.CompleteExternalAsync("google", "subject-priority", "changed@example.test", true);
        Assert.Equal(ExternalIdentityCompletionState.Completed, first.State);
        Assert.Equal(first.AccountId, repeated.AccountId);
        Assert.Equal("first@example.test", await ScalarAsync<string>(owner, "select email from accounts where id = @account", ("account", first.AccountId!.Value)));

        var completions = await Task.WhenAll(
            service.CompleteExternalAsync("google", "concurrent-subject", "concurrent@example.test", true),
            service.CompleteExternalAsync("google", "concurrent-subject", "concurrent@example.test", true));
        Assert.All(completions, completion => Assert.Equal(ExternalIdentityCompletionState.Completed, completion.State));
        Assert.Single(completions.Select(completion => completion.AccountId).Distinct());
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from account_identities where provider = 'google' and subject = 'concurrent-subject'"));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from accounts where email = 'concurrent@example.test'"));
    }

    [SkippableFact]
    public async Task Verified_email_links_a_live_account_while_unverified_email_creates_subject_only_account()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var existing = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into accounts (id, email) values (@account, 'held@example.test')", ("account", existing));
        var service = CreateService();

        var verified = await service.CompleteExternalAsync("google", "verified-held", "held@example.test", true);
        var unverified = await service.CompleteExternalAsync("google", "unverified-held", "held@example.test", false);
        var created = await service.CompleteExternalAsync("google", "verified-new", "new@example.test", true);

        Assert.Equal(existing, verified.AccountId);
        Assert.NotEqual(existing, unverified.AccountId);
        Assert.True(await ScalarAsync<bool>(owner, "select email is null from accounts where id = @account", ("account", unverified.AccountId!.Value)));
        Assert.Equal("new@example.test", await ScalarAsync<string>(owner, "select email from accounts where id = @account", ("account", created.AccountId!.Value)));
    }

    [SkippableFact]
    public async Task Deleted_identity_is_unavailable()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var deleted = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into accounts (id, deleted_at) values (@account, now())", ("account", deleted));
        await ExecuteAsync(owner, "insert into account_identities (provider, subject, account_id) values ('google', 'deleted-subject', @account)", ("account", deleted));

        var result = await CreateService().CompleteExternalAsync("google", "deleted-subject", null, false);
        Assert.Equal(ExternalIdentityCompletionState.Unavailable, result.State);
        Assert.Null(result.AccountId);
    }

    private ExternalIdentityService CreateService() => new(new DatabaseSources(
        fixture.ConnectionStringForRole("quesshi_app"),
        fixture.ConnectionStringForRole("quesshi_system")));

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await new MigrationRunner(MigrationRunner.Discover(typeof(ITenants).Assembly)).RunAsync(owner);
        await ExecuteAsync(owner, "delete from account_identities; delete from accounts");
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
}
