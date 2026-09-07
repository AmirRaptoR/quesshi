using System.Net;
using System.Net.Http.Json;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

/// <summary>
/// Covers the JWT-over-WebSocket hook added for the live-duel hub: <c>access_token</c> in the query
/// string authenticates requests under <c>/hub</c> and nowhere else, and the three ways a token can
/// be wrong (missing, expired, wrong scheme) are all rejected. LiveHub itself lands with issue #10's
/// ILiveMatchGrain; these probes stand in for "a hub" and "an ordinary endpoint" so this can be
/// proven now, against exactly the code Program.cs will run.
/// </summary>
public class HubAuthenticationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_valid_player_token_in_the_query_string_authenticates_a_hub_path()
    {
        await using var host = new AuthTestHost();
        var token = host.TokenIssuer.Issue(Player.Register("p1", "amir@example.com", "Amir", Language.En, T0));

        var response = await host.Client.GetAsync($"/hub/live/probe?access_token={token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProbeResult>();
        Assert.Equal("p1", body!.PlayerId);
    }

    /// <summary>The same id an HTTP endpoint sees for the same token — same claim, same extension method.</summary>
    [Fact]
    public async Task The_query_string_token_yields_the_same_player_id_as_the_authorization_header_does_on_http()
    {
        await using var host = new AuthTestHost();
        var token = host.TokenIssuer.Issue(Player.Register("p1", "amir@example.com", "Amir", Language.En, T0));

        using var headerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/probe");
        headerRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var viaHeader = await (await host.Client.SendAsync(headerRequest)).Content.ReadFromJsonAsync<ProbeResult>();

        var viaQueryString = await (await host.Client.GetAsync($"/hub/live/probe?access_token={token}"))
            .Content.ReadFromJsonAsync<ProbeResult>();

        Assert.Equal(viaHeader!.PlayerId, viaQueryString!.PlayerId);
    }

    [Fact]
    public async Task No_token_is_rejected()
    {
        await using var host = new AuthTestHost();
        var response = await host.Client.GetAsync("/hub/live/probe");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        await using var host = new AuthTestHost();
        var expired = new Quesshi.Server.Auth.TokenIssuer(new Quesshi.Server.Auth.JwtOptions
        {
            Key = AuthTestHost.ValidKey,
            Issuer = "quesshi",
            Audience = "quesshi",
            Days = -1 // already expired the moment it's issued
        }).Issue(Player.Register("p1", "amir@example.com", "Amir", Language.En, T0));

        var response = await host.Client.GetAsync($"/hub/live/probe?access_token={expired}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_the_admin_key_is_rejected_on_the_player_scheme()
    {
        await using var host = new AuthTestHost();
        var adminToken = host.AdminTokenIssuer.Issue(AdminUser.Create("a1", "admin", "admin@example.com", "hash", T0));

        var response = await host.Client.GetAsync($"/hub/live/probe?access_token={adminToken}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The one rule this whole hook exists to enforce: query-string tokens are for /hub only.</summary>
    [Fact]
    public async Task A_token_in_the_query_string_of_a_non_hub_path_is_ignored()
    {
        await using var host = new AuthTestHost();
        var token = host.TokenIssuer.Issue(Player.Register("p1", "amir@example.com", "Amir", Language.En, T0));

        var response = await host.Client.GetAsync($"/api/probe?access_token={token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record ProbeResult(string? PlayerId);
}
