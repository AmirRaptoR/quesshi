using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Quesshi.Application;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
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
        builder.Services.AddSingleton<IGrainFactory>(_ => null!);
        builder.Services.AddSingleton<IClock>(new TestClock(DateTimeOffset.UnixEpoch));
        builder.Services.AddSingleton<ISessionSnapshotLoader>(_ => null!);
        builder.Services.AddSingleton<IPlayerSessionResolver>(_ => null!);
        builder.Services.AddSingleton(new PlayerAntiforgery(new EphemeralDataProtectionProvider(), new TestClock(DateTimeOffset.UnixEpoch)));
        using var app = builder.Build();

        app.MapRoutes();

        var command = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText is "/api/sessions/{id:guid}/commands");
        Assert.Equal("/api/sessions/{id:guid}/commands", command.RoutePattern.RawText);
        Assert.NotNull(command.Metadata.GetMetadata<AllowGuestsMetadata>());
    }
}
