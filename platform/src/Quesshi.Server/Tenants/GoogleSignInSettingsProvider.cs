using System.Text.Json;
using Quesshi.Features.SignIn.Google;
using Quesshi.Plugin.Api;

namespace Quesshi.Server.Tenants;

/// <summary>Reads the enabled Google capability settings for a resolved tenant.</summary>
internal sealed class GoogleSignInSettingsProvider(ITenantDb tenantDb) : IGoogleSignInSettings
{
    public async Task<GoogleSignInSettings?> GetAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null)
        {
            return null;
        }

        return await tenantDb.InTenantAsync(tenant.Id, async transaction =>
        {
            var row = await transaction.QuerySingleOrDefaultAsync<GoogleSettingsRow>("""
                select settings as Settings
                from tenant_capabilities
                where type = 'signin' and key = 'google' and entitled and enabled
                """, cancellationToken: cancellationToken);
            return row is null
                ? null
                : JsonSerializer.Deserialize<GoogleSignInSettings>(row.Settings);
        }, cancellationToken: cancellationToken, readOnly: true);
    }

    private sealed record GoogleSettingsRow(JsonElement Settings);
}
