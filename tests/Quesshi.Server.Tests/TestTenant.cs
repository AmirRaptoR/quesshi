using Quesshi.Application.Ports;
using Microsoft.AspNetCore.Builder;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Tests;

internal static class TestTenant
{
    public static TenantSettingsDto SettingsFor(IEnumerable<string> enabledModes) => new("quesshi", "Quesshi", new TenantBrandSettingsDto(
        "quesshi", new Dictionary<string, string>(), ["en"], [.. enabledModes], new Dictionary<string, string>()));

    public static IApplicationBuilder UseAllModes(this IApplicationBuilder app)
        => app.UseModes("async", "live", "voting");

    public static IApplicationBuilder UseModes(this IApplicationBuilder app, params string[] enabledModes)
    {
        app.Use(async (context, next) =>
        {
            context.Items[typeof(TenantSettingsDto)] = SettingsFor(enabledModes);
            await next();
        });
        return app;
    }
}
