using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Quesshi.Domain;

namespace Quesshi.Server.Auth;

public sealed class TokenIssuer(JwtOptions options)
{
    public const string GuestClaim = "guest";

    public SymmetricSecurityKey SigningKey { get; } = new(Encoding.UTF8.GetBytes(options.Key.PadRight(32, '.')));

    /// <summary>Guests get the same token with one extra claim; the API narrows what it opens.</summary>
    public string Issue(Player player)
    {
        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, player.Id),
            new(ClaimTypes.NameIdentifier, player.Id),
            new("name", player.DisplayName)
        ];

        if (player.IsGuest) claims.Add(new Claim(GuestClaim, "1"));

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(options.Days),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
