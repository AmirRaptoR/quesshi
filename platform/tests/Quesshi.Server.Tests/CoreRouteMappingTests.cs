using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Application;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;

namespace Quesshi.Server.Tests;

public sealed class CoreRouteMappingTests
{
    [Fact]
    public void Session_commands_are_mounted_once_under_api_and_allow_guests()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(FeatureRegistry.Create([]));
        using var app = builder.Build();

        app.MapRoutes();

        var command = Assert.Single(app.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("sessions", StringComparison.Ordinal) is true));
        Assert.Equal("/api/sessions/{id:guid}/commands", command.RoutePattern.RawText);
        Assert.NotNull(command.Metadata.GetMetadata<AllowGuestsMetadata>());
    }
}
