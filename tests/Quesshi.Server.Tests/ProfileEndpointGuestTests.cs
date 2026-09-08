using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// The two HTTP-level criteria issue #54 asks for, driven against a real endpoint pipeline
/// (<see cref="GameApiTestHost"/>) rather than by calling <c>GameEndpoints</c>' handler directly, since
/// both bugs live in wiring a handler call cannot exercise: the guest gate is an endpoint filter that
/// runs before any handler at all, and the avatar palette check is the new validation this issue adds
/// to that same handler.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class ProfileEndpointGuestTests(ClusterFixture fixture) : IAsyncDisposable
{
    private readonly GameApiTestHost _host = new(fixture.Cluster);

    private HttpClient AuthedClient(Player player)
    {
        var client = _host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.TokenIssuer.Issue(player));
        return client;
    }

    /// <summary>
    /// The actual 403 this issue fixes: before <c>.WithMetadata(new AllowGuest())</c> landed on
    /// <c>PUT /api/me</c>, the endpoint group's filter (<c>GameEndpoints.cs:23-28</c>) refused every
    /// guest request that did not carry it — which blocked exactly the lobby page's guest name/avatar
    /// form issue #53 shipped against the intended contract.
    /// </summary>
    [Fact]
    public async Task A_guest_token_can_change_its_name_and_avatar()
    {
        var guest = Player.Guest($"pe-guest-{Guid.NewGuid():N}", "Guest", Language.En, DateTimeOffset.UtcNow);
        Shared.Players.Items.Add(guest);
        using var client = AuthedClient(guest);

        var response = await client.PutAsJsonAsync("/api/me", new UpdateProfileDto("New Guest Name", "en", AvatarPalette.Seeds[3]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeDto>();
        Assert.NotNull(me);
        Assert.Equal("New Guest Name", me!.DisplayName);
        Assert.Equal(AvatarPalette.Seeds[3], me.AvatarSeed);

        // Persisted, not just echoed back: a second read confirms the write actually landed.
        var again = await client.GetFromJsonAsync<MeDto>("/api/me");
        Assert.Equal("New Guest Name", again!.DisplayName);
        Assert.Equal(AvatarPalette.Seeds[3], again.AvatarSeed);
    }

    [Fact]
    public async Task A_signed_in_player_editing_their_profile_is_not_blocked_by_the_guest_gate()
    {
        var player = Player.Register($"pe-player-{Guid.NewGuid():N}", $"pe-{Guid.NewGuid():N}@example.com", "Amir", Language.En, DateTimeOffset.UtcNow);
        Shared.Players.Items.Add(player);
        using var client = AuthedClient(player);

        var response = await client.PutAsJsonAsync("/api/me", new UpdateProfileDto("Amir Renamed", "en", AvatarPalette.Seeds[0]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The client only ever offers <see cref="AvatarPalette.Seeds"/> (issue #53's lobby form), but
    /// nothing stops a hand-built request from sending anything else, so the server has to be the
    /// actual source of truth — the same reason the name-length check is not merely a client-side nicety.
    /// </summary>
    [Fact]
    public async Task An_avatar_seed_outside_the_palette_is_refused()
    {
        var player = Player.Register($"pe-badavatar-{Guid.NewGuid():N}", $"pe-{Guid.NewGuid():N}@example.com", "Amir", Language.En, DateTimeOffset.UtcNow);
        Shared.Players.Items.Add(player);
        using var client = AuthedClient(player);

        var response = await client.PutAsJsonAsync("/api/me", new UpdateProfileDto("Amir", "en", "not-a-real-seed"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Refused before it ever reached the grain: the original avatar (the player id, per
        // Player's own constructor) must be untouched.
        var me = await client.GetFromJsonAsync<MeDto>("/api/me");
        Assert.Equal(player.Id, me!.AvatarSeed);
    }

    [Fact]
    public async Task A_null_avatar_seed_leaves_the_avatar_untouched()
    {
        var player = Player.Register($"pe-nullavatar-{Guid.NewGuid():N}", $"pe-{Guid.NewGuid():N}@example.com", "Amir", Language.En, DateTimeOffset.UtcNow);
        Shared.Players.Items.Add(player);
        using var client = AuthedClient(player);

        var response = await client.PutAsJsonAsync("/api/me", new UpdateProfileDto("Amir", "en", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeDto>();
        Assert.Equal(player.Id, me!.AvatarSeed);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
