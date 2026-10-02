using Quesshi.Application.Ports;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantSettings(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tenant/settings", (HttpContext context) =>
            Results.Ok((TenantSettingsDto)context.Items[typeof(TenantSettingsDto)]!));
        endpoints.MapGet("/api/tenant/modes", (HttpContext context) =>
            Results.Ok(new TenantModesDto(((TenantSettingsDto)context.Items[typeof(TenantSettingsDto)]!).Brand.EnabledModes)));
        return endpoints;
    }
}
