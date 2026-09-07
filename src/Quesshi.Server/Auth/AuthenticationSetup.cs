using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Quesshi.Server.Auth;

public static class AuthenticationSetup
{
    /// <summary>
    /// Two schemes, two audiences, two signing keys. A player token cannot be presented as an admin
    /// token even if something else goes wrong, because it will not validate against the admin
    /// scheme. Extracted out of Program.cs so a test host can register exactly what production does,
    /// rather than a copy that silently drifts from it.
    /// </summary>
    public static AuthenticationBuilder AddQuesshiAuthentication(this IServiceCollection services,
        JwtOptions jwtOptions, AdminAuthOptions adminAuthOptions, TokenIssuer tokenIssuer, AdminTokenIssuer adminTokenIssuer)
        => services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwtOptions.Issuer,
                    ValidAudience = jwtOptions.Audience,
                    IssuerSigningKey = tokenIssuer.SigningKey,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };

                // Browsers cannot set an Authorization header on a WebSocket handshake, so SignalR
                // sends the token as ?access_token= instead. Reading that query string for every
                // request would let it authenticate ordinary HTTP calls too, so this is scoped to
                // paths under /hub — the only place a socket is ever negotiated.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hub"))
                            context.Token = accessToken;

                        return Task.CompletedTask;
                    }
                };
            })
            .AddJwtBearer(AdminTokenIssuer.Scheme, options => options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = adminAuthOptions.Issuer,
                ValidAudience = AdminTokenIssuer.Audience,
                IssuerSigningKey = adminTokenIssuer.SigningKey,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            });
}
