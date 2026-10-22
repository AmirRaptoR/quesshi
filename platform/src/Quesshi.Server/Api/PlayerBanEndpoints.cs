using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Tenant moderator controls for player access bans.</summary>
public sealed class PlayerBanEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        var players = routes.PlayerOperationRoutes().MapGroup("/players");
        players.MapPost("/{id:guid}/ban", BanAsync).WithMetadata(new AdminAuditMetadata("admin.player.ban", "player"));
        players.MapDelete("/{id:guid}/ban", UnbanAsync).WithMetadata(new AdminAuditMetadata("admin.player.unban", "player"));
    }

    private static async Task<IResult> BanAsync(HttpContext context, Guid id, IPlayerBanManager players, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        return Answer(await players.BanAsync(tenant.Id, id, cancellationToken));
    }

    private static async Task<IResult> UnbanAsync(HttpContext context, Guid id, IPlayerBanManager players, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        return Answer(await players.UnbanAsync(tenant.Id, id, cancellationToken));
    }

    private static IResult Answer(PlayerBanChangeResult result) => result.Status switch
    {
        PlayerBanChangeStatus.Success => Results.NoContent(),
        PlayerBanChangeStatus.NotFound => Results.NotFound(),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
    };
}
