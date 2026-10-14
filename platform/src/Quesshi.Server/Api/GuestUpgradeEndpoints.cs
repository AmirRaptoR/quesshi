using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quesshi.Application.Identity;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Otp;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Turns a guest's existing tenant player into an account-backed player.</summary>
public sealed class GuestUpgradeEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes) =>
        routes.PlayerRoutes.MapPost("/me/upgrade", UpgradeAsync).AllowGuests();

    private static async Task<IResult> UpgradeAsync(
        UpgradeRequest request,
        HttpContext context,
        IPlayerSessionIssuer sessions,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context)
            ?? throw new InvalidOperationException("Guest upgrade requires tenant resolution.");
        var player = context.Player()
            ?? throw new InvalidOperationException("Guest upgrade requires player authentication.");
        if (!player.IsGuest) return Results.Forbid();

        var service = Create(context.RequestServices);
        var result = await service.VerifyAndAttachAsync(
            tenant.Id,
            player.PlayerId,
            request.Email ?? string.Empty,
            request.Code ?? string.Empty,
            cancellationToken);

        if (result.State == GuestUpgradeState.AddressTaken)
        {
            return Results.Conflict(new { error = "address_taken" });
        }
        if (result.State != GuestUpgradeState.Upgraded || result.AccountId is null)
        {
            // Guest availability and code failures deliberately share the same response so
            // this route cannot reveal account or player state to an unverified caller.
            return Results.Unauthorized();
        }

        await service.RevokeGuestSessionsAsync(tenant.Id, player.PlayerId, cancellationToken);
        await sessions.IssueAccountAsync(context, tenant.Id, result.AccountId.Value, cancellationToken);
        return Results.Ok(new { status = "upgraded" });
    }

    private static GuestUpgradeService Create(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var options = new EmailAuthOptions(
            configuration.GetValue("EmailAuth:EmailRateLimit", EmailAuthOptions.Default.EmailLimit),
            configuration.GetValue("EmailAuth:AddressRateLimit", EmailAuthOptions.Default.AddressLimit),
            TimeSpan.FromSeconds(configuration.GetValue("EmailAuth:RateLimitWindowSeconds", (int)EmailAuthOptions.Default.RateLimitWindow.TotalSeconds)),
            TimeSpan.FromSeconds(configuration.GetValue("EmailAuth:ProfileTokenLifetimeSeconds", (int)EmailAuthOptions.Default.ProfileTokenLifetime.TotalSeconds)));
        var emailAuth = new EmailAuthService(
            services.GetRequiredService<DatabaseSources>(),
            services.GetRequiredService<ITenantDb>(),
            EmailAuthService.SecretFromEnvironment(),
            options,
            new EmailCodeDelivery(configuration, services.GetRequiredService<ILogger<EmailCodeDelivery>>()));
        return new GuestUpgradeService(
            emailAuth,
            services.GetRequiredService<DatabaseSources>(),
            services.GetRequiredService<ITenantDb>(),
            services.GetRequiredService<IEvents>());
    }

    private sealed record UpgradeRequest(string? Email, string? Code);
}
