using Quesshi.Plugin.Api;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Security;

/// <summary>Adapts core tenant and session services for external sign-in plugins.</summary>
internal sealed class ExternalSignInContext(IPlayerSessionIssuer sessions) : IExternalSignInContext
{
    public ExternalIdentityTenant? GetTenant(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        return tenant is null ? null : new ExternalIdentityTenant(tenant.Id, tenant.DefaultLanguage);
    }

    public Task IssueAccountSessionAsync(HttpContext context, Guid tenantId, Guid accountId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return sessions.IssueAccountAsync(context, tenantId, accountId, cancellationToken);
    }
}
