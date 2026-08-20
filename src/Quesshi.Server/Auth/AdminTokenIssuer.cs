using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Quesshi.Domain;

namespace Quesshi.Server.Auth;

/// <summary>
/// Issues administrator tokens. Deliberately a different audience and a different signing key from
/// the player token: a game session can never be mistaken for an admin session, whatever else goes
/// wrong. Sessions are short, because an admin token is worth far more than a player one.
/// </summary>
public sealed class AdminTokenIssuer(AdminAuthOptions options)
{
    public const string Scheme = "AdminBearer";
    public const string Audience = "quesshi-admin";

    public SymmetricSecurityKey SigningKey { get; } = new(Encoding.UTF8.GetBytes(options.Key.PadRight(32, '.')));

    public string Issue(AdminUser user)
    {
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id),
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim("typ", "admin")
            ],
            expires: DateTime.UtcNow.AddHours(options.SessionHours),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
