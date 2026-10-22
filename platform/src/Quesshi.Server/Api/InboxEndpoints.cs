using Microsoft.AspNetCore.Mvc;
using Quesshi.Application.Inbox;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Tenant-scoped, durable inbox reads for the authenticated player.</summary>
public sealed class InboxEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes) =>
        routes.PlayerRoutes.MapGet("/me/inbox", GetAsync).AllowGuests();

    private static async Task<IResult> GetAsync(
        HttpContext context,
        [FromServices] InboxReader inbox,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("Inbox access requires tenant resolution.");
        var player = context.Player() ?? throw new InvalidOperationException("Inbox access requires player authentication.");
        return Results.Ok(await inbox.ReadAsync(tenant.Id, player.PlayerId, player.IsGuest, cancellationToken));
    }
}
