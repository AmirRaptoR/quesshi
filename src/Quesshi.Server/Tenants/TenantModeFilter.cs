using Quesshi.Application.Ports;

namespace Quesshi.Server.Tenants;

/// <summary>Rejects game routes that are not enabled for the host-resolved tenant.</summary>
public sealed class TenantModeFilter(string mode) : IEndpointFilter
{
    // A null context only occurs when a hub method is called directly in isolation; routed HTTP and
    // SignalR requests always carry the host-resolved settings from TenantResolutionMiddleware.
    public static bool IsEnabled(HttpContext? context, string mode)
        => context is null || context.Items[typeof(TenantSettingsDto)] is TenantSettingsDto tenant
           && tenant.Brand.EnabledModes.Contains(mode, StringComparer.OrdinalIgnoreCase);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!IsEnabled(context.HttpContext, mode))
            return Results.NotFound(new { error = "mode_disabled" });

        return await next(context);
    }
}
