using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Infrastructure;
using Quesshi.Server.Auth;

namespace Quesshi.Server.Tenants;

/// <summary>Creates the configured first administrator independently inside each tenant database.</summary>
public static class TenantAdminBootstrapper
{
    public static async Task EnsureFirstAdminAsync(string tenantId, TenantContext tenantContext,
        IAdminUserRepository admins, AdminAuthService auth, AdminAuthOptions options, ILogger logger)
    {
        using var tenant = tenantContext.Enter(tenantId);
        if (await admins.CountAsync() != 0) return;

        var generated = string.IsNullOrWhiteSpace(options.BootstrapPassword);
        var password = generated
            ? "quesshi-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()
            : options.BootstrapPassword!;

        await auth.CreateAsync(options.BootstrapUsername, options.BootstrapEmail, password, mustChangePassword: generated);

        if (generated)
            logger.LogWarning("Created the first administrator for tenant {TenantId}: username \"{Username}\", password \"{Password}\" — sign in at /admin and change it.",
                tenantId, options.BootstrapUsername, password);
        else
            logger.LogInformation("Created the first administrator \"{Username}\" for tenant {TenantId} from configuration.",
                options.BootstrapUsername, tenantId);
    }
}
