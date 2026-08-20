using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Quesshi.Domain;
using Quesshi.Server.Api;
using Quesshi.Server.Auth;

namespace Quesshi.Server.Tests;

/// <summary>
/// The guest gate reads one claim out of the token and nothing else, so if the claim is wrong the
/// gate is wrong — whatever the endpoints say.
/// </summary>
public class GuestTokenTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TokenIssuer Issuer = new(new JwtOptions
    {
        Key = "a-test-signing-key-long-enough-to-use",
        Issuer = "quesshi",
        Audience = "quesshi",
        Days = 1
    });

    private static ClaimsPrincipal Read(string token)
        => new(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, "test"));

    [Fact]
    public void A_guest_token_says_so()
    {
        var token = Issuer.Issue(Player.Guest("g1", "Sara", Language.En, T0));
        Assert.True(Read(token).IsGuest());
    }

    [Fact]
    public void An_account_token_does_not()
    {
        var token = Issuer.Issue(Player.Register("p1", "amir@example.com", "Amir", Language.En, T0));
        Assert.False(Read(token).IsGuest());
    }

    /// <summary>A guest is still a player: the rest of the API has to be able to identify them.</summary>
    [Fact]
    public void A_guest_token_still_carries_the_player_id()
    {
        var token = Issuer.Issue(Player.Guest("g1", "Sara", Language.En, T0));
        Assert.Equal("g1", Read(token).PlayerId());
    }
}
