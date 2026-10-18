using Microsoft.AspNetCore.Http;
using Quesshi.Application;
using Quesshi.Application.Profiles;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Guest entry and the current player's tenant-scoped profile routes.</summary>
public sealed class GuestProfileEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        routes.OpenRoutes.MapPost("/auth/guest", CreateGuestAsync);
        routes.PlayerRoutes.MapGet("/me", GetProfileAsync).AllowGuests();
        routes.PlayerRoutes.MapPut("/me", UpdateProfileAsync).AllowGuests();
    }

    private static async Task<IResult> CreateGuestAsync(
        HttpContext context,
        GuestRequest? request,
        ICapabilities capabilities,
        ITenantDb tenantDb,
        IPlayerSessionIssuer sessions,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("Guest entry requires tenant resolution.");
        if (!await capabilities.IsOnAsync(tenant.Id, CapabilityType.Switch, "guests", cancellationToken)) return Results.NotFound();
        if (!PlayerProfiles.TryNormalizeDisplayName(request?.DisplayName, out var displayName)) return Results.BadRequest(new { error = "invalid_profile" });

        var profile = await PlayerProfiles.CreateGuestAsync(tenantDb, tenant.Id, displayName, cancellationToken);
        await sessions.IssueGuestAsync(context, tenant.Id, profile.Id, cancellationToken);
        return Results.Created("/api/me", profile);
    }

    private static async Task<IResult> GetProfileAsync(HttpContext context, ITenantDb tenantDb, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("Profile access requires tenant resolution.");
        var player = context.Player() ?? throw new InvalidOperationException("Profile access requires player authentication.");
        var profile = await PlayerProfiles.GetAsync(tenantDb, tenant.Id, player.PlayerId, cancellationToken);
        return profile is null ? Results.NotFound() : Results.Ok(profile);
    }

    private static async Task<IResult> UpdateProfileAsync(
        HttpContext context,
        UpdateProfileRequest? request,
        ITenantDb tenantDb,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("Profile access requires tenant resolution.");
        var player = context.Player() ?? throw new InvalidOperationException("Profile access requires player authentication.");
        var result = await PlayerProfiles.UpdateAsync(
            tenantDb,
            tenant.Id,
            player.PlayerId,
            request?.DisplayName,
            request?.AvatarSeed,
            request?.Lang,
            cancellationToken);
        return result.Status switch
        {
            ProfileUpdateStatus.Updated => Results.Ok(result.Profile),
            ProfileUpdateStatus.Missing => Results.NotFound(),
            _ => Results.BadRequest(new { error = "invalid_profile" })
        };
    }

    private sealed record GuestRequest(string? DisplayName);
    private sealed record UpdateProfileRequest(string? DisplayName, string? AvatarSeed, string? Lang);
}
