using System.Security.Cryptography;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Identity;

namespace Quesshi.Server.Tenants;

internal sealed record TenantAdminBootstrapResult(bool Created, TenantAdminCredentials? Credentials = null);

internal sealed record TenantAdminCredentials(string Username, string Email, string Password);

/// <summary>Creates the one-time owner credential for tenants that do not have an administrator.</summary>
internal sealed class TenantAdminBootstrapper(ISystemDb database, AdminAccountCreator accounts)
{
    private const int CredentialAttempts = 4;

    public async Task EnsureAllAsync(TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var tenants = await database.QueryAsync<TenantRow>("select id as Id from tenants order by id", cancellationToken: cancellationToken);
        foreach (var tenant in tenants)
        {
            await EnsureAsync(tenant.Id, output, cancellationToken);
        }
    }

    public async Task<TenantAdminBootstrapResult> EnsureAsync(
        Guid tenantId,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        for (var attempt = 0; attempt < CredentialAttempts; attempt++)
        {
            var credentials = CreateCredentials();
            var result = await accounts.CreateAsync(new(
                credentials.Username,
                credentials.Email,
                credentials.Password,
                IsPlatform: false,
                MustChangePassword: true,
                TenantId: tenantId,
                OnlyIfTenantHasNoAdmin: true), cancellationToken);

            switch (result.Status)
            {
                case AdminAccountCreationStatus.Created:
                    await output.WriteLineAsync($"Created temporary administrator for tenant {tenantId}: username {credentials.Username}; password {credentials.Password}. Change this password before administering the tenant.");
                    return new TenantAdminBootstrapResult(true, credentials);
                case AdminAccountCreationStatus.TenantAlreadyHasAdmin:
                    return new TenantAdminBootstrapResult(false);
                case AdminAccountCreationStatus.DuplicateIdentity:
                    continue;
                case AdminAccountCreationStatus.TenantNotFound:
                    throw new InvalidOperationException("Cannot bootstrap an administrator for a tenant that does not exist.");
                default:
                    throw new InvalidOperationException("Could not create the initial tenant administrator.");
            }
        }

        throw new InvalidOperationException("Could not allocate unique temporary administrator credentials.");
    }

    private static TenantAdminCredentials CreateCredentials()
    {
        var usernameSuffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var passwordSuffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
        return new TenantAdminCredentials(
            $"bootstrap-{usernameSuffix}",
            $"bootstrap-{usernameSuffix}@bootstrap.invalid",
            $"Change-{passwordSuffix}-2026!");
    }

    private sealed record TenantRow(Guid Id);
}
