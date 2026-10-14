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
using Quesshi.Server.Streaming;

namespace Quesshi.Server.Tests;

public sealed class CoreRouteMappingTests
{
    [Fact]
    public void Guest_and_profile_routes_are_mounted_and_profile_routes_allow_guests()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(FeatureRegistry.Create([]));
        builder.Services.AddSingleton<IGrainFactory>(_ => null!);
        builder.Services.AddSingleton<IClock>(new TestClock(DateTimeOffset.UnixEpoch));
        builder.Services.AddSingleton<ISessionSnapshotLoader>(_ => null!);
        builder.Services.AddSingleton<IPlayerSessionResolver>(_ => null!);
        builder.Services.AddSingleton<IPlayerSessionIssuer>(_ => null!);
        builder.Services.AddSingleton<ITenantDb>(_ => null!);
        builder.Services.AddSingleton<ICapabilities>(_ => null!);
        builder.Services.AddSingleton(new PlayerAntiforgery(new EphemeralDataProtectionProvider(), new TestClock(DateTimeOffset.UnixEpoch)));
        using var app = builder.Build();

        app.MapRoutes();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText is "/api/auth/guest");
        var profileRoutes = endpoints.Where(endpoint => endpoint.RoutePattern.RawText is "/api/me").ToArray();
        Assert.Equal(3, profileRoutes.Length);
        Assert.Equal(2, profileRoutes.Count(endpoint => endpoint.Metadata.GetMetadata<AllowGuestsMetadata>() is not null));
        Assert.Single(profileRoutes, endpoint => endpoint.Metadata.GetMetadata<AllowGuestsMetadata>() is null);
        var upgrade = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText is "/api/me/upgrade");
        Assert.NotNull(upgrade.Metadata.GetMetadata<AllowGuestsMetadata>());
    }

    [Fact]
    public void Session_stream_is_mounted_under_the_api_prefix()
    {
        var builder = WebApplication.CreateSlimBuilder();
        using var app = builder.Build();

        app.MapStream();

        var stream = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText is "/api/stream");
        Assert.Equal("/api/stream", stream.RoutePattern.RawText);
    }

    [Fact]
    public void Session_commands_are_mounted_once_under_api_and_allow_guests()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(FeatureRegistry.Create([]));
        builder.Services.AddSingleton<IGrainFactory>(_ => null!);
        builder.Services.AddSingleton<IClock>(new TestClock(DateTimeOffset.UnixEpoch));
        builder.Services.AddSingleton<ISessionSnapshotLoader>(_ => null!);
        builder.Services.AddSingleton<IPlayerSessionResolver>(_ => null!);
        builder.Services.AddSingleton<IPlayerSessionIssuer>(_ => null!);
        builder.Services.AddSingleton<ITenantDb>(_ => null!);
        builder.Services.AddSingleton<ICapabilities>(_ => null!);
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
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText).ToHashSet();
        Assert.Contains("/api/auth/code", routes);
        Assert.Contains("/api/auth/verify", routes);
        Assert.Contains("/api/auth/profile", routes);
    }
}
