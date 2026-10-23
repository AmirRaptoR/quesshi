using Microsoft.AspNetCore.Http;

namespace Quesshi.Plugin.Api;

/// <summary>Provides a plugin route with the tenant selected by the core request pipeline.</summary>
public interface ITenantRequestContext
{
    /// <summary>Gets the selected tenant identifier, if request tenant resolution succeeded.</summary>
    Guid? GetTenantId(HttpContext context);
}
