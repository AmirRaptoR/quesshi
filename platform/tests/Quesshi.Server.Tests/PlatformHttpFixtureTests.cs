using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class PlatformHttpFixtureTests(PostgresFixture postgres) : IAsyncLifetime
{
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null) return;

        _host = new PlatformHttpFixture([new IdentityRoutesFeature()]);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Concurrent_fixtures_use_isolated_databases_and_dynamic_ports()
    {
        Skip.If(postgres.ConnectionString is null, "PostgreSQL is not configured.");
        await using var first = new PlatformHttpFixture([new IdentityRoutesFeature()]);
        await using var second = new PlatformHttpFixture([new IdentityRoutesFeature()]);

        await Task.WhenAll(first.StartAsync(), second.StartAsync());
        using var firstResponse = await first.Anonymous.PostAsync("/api/x/fixture_identity/open", null);
        using var secondResponse = await second.Anonymous.PostAsync("/api/x/fixture_identity/open", null);
        var identities = await Task.WhenAll(
            firstResponse.Content.ReadFromJsonAsync<ObservedIdentity>(),
            secondResponse.Content.ReadFromJsonAsync<ObservedIdentity>());

        Assert.NotEqual(first.DatabaseName, second.DatabaseName);
        Assert.Equal(6, new[] { first.HttpPort, first.SiloPort, first.GatewayPort, second.HttpPort, second.SiloPort, second.GatewayPort }.Distinct().Count());
        Assert.All(identities, identity => Assert.Equal("anonymous", identity?.Kind));
    }

    [SkippableFact]
    public async Task Anonymous_client_observes_anonymous_identity()
    {
        var identity = await PostIdentityAsync("/api/x/fixture_identity/open", Host.Anonymous);

        Assert.Equal("anonymous", identity.Kind);
    }

    [SkippableFact]
    public async Task Guest_client_observes_guest_identity()
    {
        var identity = await PostIdentityAsync("/api/x/fixture_identity/guest", Host.Guest);

        Assert.Equal("guest", identity.Kind);
    }

    [SkippableFact]
    public async Task Player_client_observes_player_identity()
    {
        var identity = await PostIdentityAsync("/api/x/fixture_identity/player", Host.Player);

        Assert.Equal("player", identity.Kind);
    }

    [SkippableFact]
    public async Task Moderator_client_observes_moderator_authority()
    {
        var identity = await PostIdentityAsync("/api/admin/x/fixture_identity/moderator", Host.Moderator);

        Assert.Equal("moderator", identity.Kind);
    }

    [SkippableFact]
    public async Task Editor_client_observes_editor_authority()
    {
        var identity = await PostIdentityAsync("/api/admin/x/fixture_identity/editor", Host.Editor);

        Assert.Equal("editor", identity.Kind);
    }

    [SkippableFact]
    public async Task Owner_client_observes_owner_authority()
    {
        var identity = await PostIdentityAsync("/api/admin/x/fixture_identity/owner", Host.Owner);

        Assert.Equal("owner", identity.Kind);
    }

    [SkippableFact]
    public async Task Platform_administrator_client_observes_platform_authority()
    {
        var identity = await PostIdentityAsync("/api/admin/x/fixture_identity/owner", Host.PlatformAdministrator);

        Assert.Equal("platform_administrator", identity.Kind);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(postgres.ConnectionString is null, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private static async Task<ObservedIdentity> PostIdentityAsync(string path, HttpClient client)
    {
        using var response = await client.PostAsync(path, null);
        return (await response.Content.ReadFromJsonAsync<ObservedIdentity>())
            ?? throw new InvalidOperationException("The identity route returned no identity.");
    }

    private sealed class IdentityRoutesFeature : IFeature
    {
        public string Id => "fixture_identity";

        public FeatureSettings Settings => FeatureSettings.Create(new IdentityRoutesSettings(), _ => []);

        public void Configure(IFeatureBuilder feature)
        {
            feature.OpenRoutes(routes => routes.MapPost("/open", (HttpContext context) => Results.Ok(ObservedIdentity.From(context))));
            feature.PlayerRoutes(routes =>
            {
                routes.MapPost("/guest", (HttpContext context) => Results.Ok(ObservedIdentity.From(context))).AllowGuests();
                routes.MapPost("/player", (HttpContext context) => Results.Ok(ObservedIdentity.From(context)));
            });
            feature.AdminRoutes(AdminRole.Moderator, routes => routes.MapPost("/moderator", (HttpContext context) => Results.Ok(ObservedIdentity.From(context))));
            feature.AdminRoutes(AdminRole.Editor, routes => routes.MapPost("/editor", (HttpContext context) => Results.Ok(ObservedIdentity.From(context))));
            feature.AdminRoutes(AdminRole.Owner, routes => routes.MapPost("/owner", (HttpContext context) => Results.Ok(ObservedIdentity.From(context))));
        }
    }

    private sealed record IdentityRoutesSettings;
}

internal sealed record ObservedIdentity(string Kind)
{
    public static ObservedIdentity From(HttpContext context)
    {
        if (context.Admin() is { IsPlatform: true }) return new("platform_administrator");
        if (context.Admin() is { Role: { } role }) return new(role.ToString().ToLowerInvariant());
        if (context.Player() is { IsGuest: true }) return new("guest");
        if (context.Player() is not null) return new("player");
        return new("anonymous");
    }
}
