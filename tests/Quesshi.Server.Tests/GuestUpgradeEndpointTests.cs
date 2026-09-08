using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// <c>POST /api/me/upgrade</c>, issue #55: driven at the real HTTP layer, the same way
/// <see cref="ProfileEndpointGuestTests"/> drives <c>PUT /api/me</c> — the guest gate is an endpoint
/// filter that runs before any handler, and only a real pipeline proves the not-a-guest guard and the
/// fresh token's claim actually behave, neither of which a direct handler call could exercise on its
/// own the way it exercises the address-taken refusal below.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class GuestUpgradeEndpointTests(ClusterFixture fixture) : IAsyncDisposable
{
    private readonly GameApiTestHost _host = new(fixture.Cluster);

    private HttpClient AuthedClient(Player player)
    {
        var client = _host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.TokenIssuer.Issue(player));
        return client;
    }

    /// <summary>Plants a challenge directly in <see cref="GameApiTestHost.Otps"/> — the same shortcut
    /// <c>AuthServiceTests</c> takes by reading <c>_sender.LastCode</c> instead of a full request/verify
    /// round trip, since <c>POST /api/auth/otp/request</c> is not mapped on this host at all.</summary>
    private async Task<string> IssueChallengeAsync(string email, string code = "123456")
    {
        var challenge = OtpChallenge.Issue(email, code, Shared.Clock.Now);
        await _host.Otps.SaveAsync(challenge);
        return code;
    }

    private static Player NewGuest(string idSuffix, long bankedScore = 0)
    {
        var guest = Player.Guest($"gu-guest-{idSuffix}", "Guest", Language.En, DateTimeOffset.UtcNow);
        if (bankedScore > 0) guest.RecordResult(MatchOutcome.Win, bankedScore);
        return guest;
    }

    /// <summary>
    /// The acceptance criterion in full: the player id is unchanged, the token this call returns
    /// carries no guest claim, and that token actually opens an account-only endpoint — <c>POST
    /// /api/friends/{id}</c>, refused to every guest by <c>GameEndpoints</c>' own filter — which the
    /// guest's old token could never reach. Matches, friendships and stats survive because nothing
    /// about this player's row moves; see <c>PlayerGrainWriteOwnershipTests</c> for the same guarantee
    /// proved directly at the grain.
    /// </summary>
    [Fact]
    public async Task Upgrading_keeps_the_player_id_and_the_new_token_reaches_an_account_only_endpoint()
    {
        var guest = NewGuest(Guid.NewGuid().ToString("N"), bankedScore: 250);
        guest.AddFriend("someone-else");
        Shared.Players.Items.Add(guest);

        var friend = Player.Register($"gu-friend-{Guid.NewGuid():N}", $"gu-friend-{Guid.NewGuid():N}@example.com", "Friend", Language.En, DateTimeOffset.UtcNow);
        Shared.Players.Items.Add(friend);

        var email = $"gu-upgraded-{Guid.NewGuid():N}@example.com";
        var code = await IssueChallengeAsync(email);

        using var client = AuthedClient(guest);
        var response = await client.PostAsJsonAsync("/api/me/upgrade", new OtpVerifyDto(email, code));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AuthResultDto>();
        Assert.NotNull(result);
        Assert.Equal(guest.Id, result!.Me.Id);
        Assert.False(result.Me.IsGuest);
        Assert.Equal(email, result.Me.Email);
        Assert.Equal(250, result.Me.Stats.TotalScore); // banked score survives the upgrade untouched

        // The old, still-guest token could never reach this: GameEndpoints' filter forbids every guest
        // request without AllowGuest, and POST /api/friends/{id} carries none.
        using var upgraded = _host.NewClient();
        upgraded.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Token);
        var friendResponse = await upgraded.PostAsync($"/api/friends/{friend.Id}", null);
        Assert.Equal(HttpStatusCode.OK, friendResponse.StatusCode);

        // The friendship from before the upgrade is still on record, same id, same row. Read from the
        // repository directly rather than through GET /api/me's FriendsOfAsync, which silently drops
        // any friend id with no matching player record — "someone-else" is a stand-in id, not a real
        // registered player, so this is the one place worth checking the raw Friends set instead.
        var stored = await Shared.Players.GetAsync(guest.Id);
        Assert.Contains("someone-else", stored!.Friends);
    }

    /// <summary>
    /// The refusal is a clear, distinct reason (not the generic "wrong code" shape) precisely so the
    /// client can offer ordinary sign-in instead of a retry — and it must not mutate anything: not the
    /// guest (still a guest, still its old address), not the existing account it collided with.
    /// </summary>
    [Fact]
    public async Task An_address_with_an_existing_account_is_refused_without_mutating_anything()
    {
        var guest = NewGuest(Guid.NewGuid().ToString("N"));
        Shared.Players.Items.Add(guest);

        var owner = Player.Register($"gu-owner-{Guid.NewGuid():N}", $"gu-taken-{Guid.NewGuid():N}@example.com", "Owner", Language.En, DateTimeOffset.UtcNow);
        Shared.Players.Items.Add(owner);

        var code = await IssueChallengeAsync(owner.Email);

        using var client = AuthedClient(guest);
        var response = await client.PostAsJsonAsync("/api/me/upgrade", new OtpVerifyDto(owner.Email, code));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<UpgradeErrorDto>();
        Assert.Equal("addresstaken", error!.Error);

        var stillGuest = await Shared.Players.GetAsync(guest.Id);
        Assert.True(stillGuest!.IsGuest);
        Assert.Equal(guest.Email, stillGuest.Email);

        var untouchedOwner = await Shared.Players.GetAsync(owner.Id);
        Assert.Equal("Owner", untouchedOwner!.DisplayName);
    }

    [Fact]
    public async Task A_signed_in_account_cannot_call_the_upgrade_route_at_all()
    {
        var player = Player.Register($"gu-real-{Guid.NewGuid():N}", $"gu-real-{Guid.NewGuid():N}@example.com", "Amir", Language.En, DateTimeOffset.UtcNow);
        Shared.Players.Items.Add(player);

        var code = await IssueChallengeAsync("gu-someone-new@example.com");

        using var client = AuthedClient(player);
        var response = await client.PostAsJsonAsync("/api/me/upgrade", new OtpVerifyDto("gu-someone-new@example.com", code));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<UpgradeErrorDto>();
        Assert.Equal("not_a_guest", error!.Error);
    }

    /// <summary>The OTP attempt limit (<c>OtpChallenge.MaxAttempts</c>) still applies on this path, the
    /// same as ordinary sign-in — proved end to end through the real endpoint rather than only at
    /// <c>AuthService</c>, since a route that forgot to save the attempt count back would let a guest
    /// guess forever despite <c>AuthServiceTests</c> passing.</summary>
    [Fact]
    public async Task The_otp_attempt_limit_still_applies_on_the_upgrade_path()
    {
        var guest = NewGuest(Guid.NewGuid().ToString("N"));
        Shared.Players.Items.Add(guest);

        var email = $"gu-limited-{Guid.NewGuid():N}@example.com";
        var code = await IssueChallengeAsync(email);

        using var client = AuthedClient(guest);
        for (var i = 0; i < OtpChallenge.MaxAttempts; i++)
            await client.PostAsJsonAsync("/api/me/upgrade", new OtpVerifyDto(email, "000000"));

        var response = await client.PostAsJsonAsync("/api/me/upgrade", new OtpVerifyDto(email, code));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<UpgradeErrorDto>();
        Assert.Equal("toomanyattempts", error!.Error);

        var stillGuest = await Shared.Players.GetAsync(guest.Id);
        Assert.True(stillGuest!.IsGuest);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
