using Quesshi.Plugin.Api;

namespace Quesshi.Server.Tenants;

/// <summary>Adapts the resolved request tenant for plugin-owned routes.</summary>
internal sealed class TenantRequestContext : ITenantRequestContext
{
    public Guid? GetTenantId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TenantResolutionMiddleware.GetTenant(context)?.Id;
    }
}
