using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Quesshi.Application.Ports;
using Quesshi.Server.Tenants;

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
                    OnTokenValidated = context =>
                    {
                        var tenant = ResolveTenant(context.HttpContext);
                        var tokenTenant = context.Principal?.FindFirst(TokenIssuer.TenantClaim)?.Value;
                        // Pre-tenant Quesshi tokens remain valid only on Quesshi. A tenant identity
                        // is mandatory everywhere else, and tenant claims never cross host mappings.
                        if (tenant is null || (tokenTenant != tenant.Id &&
                            !(tenant.Id == "quesshi" && tokenTenant is null)))
                            context.Fail("The token belongs to a different tenant.");
                        return Task.CompletedTask;
                    },
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hub"))
                            context.Token = accessToken;

                        return Task.CompletedTask;
                    }
                };
            })
            .AddJwtBearer(AdminTokenIssuer.Scheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = adminAuthOptions.Issuer,
                    ValidAudience = AdminTokenIssuer.Audience,
                    IssuerSigningKey = adminTokenIssuer.SigningKey,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var tenant = ResolveTenant(context.HttpContext);
                        var tokenTenant = context.Principal?.FindFirst(TokenIssuer.TenantClaim)?.Value;
                        if (tenant is null || (tokenTenant != tenant.Id &&
                            !(tenant.Id == "quesshi" && tokenTenant is null)))
                            context.Fail("The token belongs to a different tenant.");
                        return Task.CompletedTask;
                    }
                };
            });

    private static TenantSettingsDto? ResolveTenant(HttpContext context)
    {
        if (context.Items[typeof(TenantSettingsDto)] is TenantSettingsDto selected) return selected;
        if (context.RequestServices.GetService<TenantRegistry>() is { } registry)
            return registry.TryResolve(context.Request.Host.Host, out var resolved) ? resolved : null;

        // Authentication-only test hosts predate tenant middleware and use TestServer's localhost.
        // A deployment always registers TenantRegistry and must resolve through its explicit host map.
        return context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? new TenantSettingsDto("quesshi", "Quesshi", new TenantBrandSettingsDto("quesshi",
                new Dictionary<string, string>(), [], [], new Dictionary<string, string>()))
            : null;
    }
}
