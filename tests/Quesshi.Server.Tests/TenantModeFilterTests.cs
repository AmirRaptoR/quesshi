using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Application.Ports;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Tests;

public sealed class TenantModeFilterTests
{
    [Fact]
    public async Task Disabled_mode_route_is_hidden_and_enabled_mode_route_is_available()
    {
        using var host = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services => services.AddRouting());
            web.Configure(app =>
            {
                app.UseRouting();
                app.Use(async (context, next) =>
                {
                    context.Items[typeof(TenantSettingsDto)] = new TenantSettingsDto("quessher", "Quessher",
                        new TenantBrandSettingsDto("quessher", new Dictionary<string, string>(), ["en"], ["voting"], new Dictionary<string, string>()));
                    await next();
                });
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/async", () => "async").AddEndpointFilter(new TenantModeFilter("async"));
                    endpoints.MapGet("/voting", () => "voting").AddEndpointFilter(new TenantModeFilter("voting"));
                });
            });
        }).Start();

        var client = host.GetTestClient();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/async")).StatusCode);
        var voting = await client.GetAsync("/voting");
        Assert.Equal(System.Net.HttpStatusCode.OK, voting.StatusCode);
        Assert.Equal("voting", await voting.Content.ReadAsStringAsync());
    }
}
