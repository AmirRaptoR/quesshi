using Quesshi.Application.Ports;
using Microsoft.AspNetCore.Builder;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Tests;

internal static class TestTenant
{
    public static readonly TenantSettingsDto Settings = new("quesshi", "Quesshi", new TenantBrandSettingsDto(
        "quesshi", new Dictionary<string, string>(), ["en"], ["async", "live", "voting"], new Dictionary<string, string>()));

    public static IApplicationBuilder UseAllModes(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            context.Items[typeof(TenantSettingsDto)] = Settings;
            await next();
        });
        return app;
    }
}
