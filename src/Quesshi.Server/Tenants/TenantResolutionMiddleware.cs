namespace Quesshi.Server.Tenants;

using Quesshi.Infrastructure;

/// <summary>Resolve only from the request Host header. Forwarded host headers are deliberately ignored.</summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantRegistry tenants, TenantContext currentTenant)
    {
        if (!tenants.TryResolve(context.Request.Host.Host, out var settings))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Items[typeof(Quesshi.Application.Ports.TenantSettingsDto)] = settings;
        using (currentTenant.Enter(settings.Id))
            await next(context);
    }
}
