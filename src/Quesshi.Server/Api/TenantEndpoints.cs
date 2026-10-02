using Quesshi.Application.Ports;

namespace Quesshi.Server.Api;

public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantSettings(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tenant/settings", (HttpContext context) =>
            Results.Ok((TenantSettingsDto)context.Items[typeof(TenantSettingsDto)]!));
        return endpoints;
    }
}
