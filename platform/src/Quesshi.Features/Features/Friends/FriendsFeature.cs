using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Features.Friends;

public sealed class FriendsFeature : IFeature
{
    public string Id => "friends";

    public FeatureSettings Settings { get; } = FeatureSettings.Create(new FriendsSettings(), static _ => []);

    public void Configure(IFeatureBuilder feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        feature.PlayerRoutes(MapPlayerRoutes);
        feature.PlayerRoutes(FriendsReadRoutes.MapPlayerRoutes);
        feature.Provides<IReadOnlyList<Guid>>("friends.ids", FriendsReadRoutes.GetFriendIdsAsync);
        feature.CleanupPlayer(CleanupPlayerAsync);
    }

    private static void MapPlayerRoutes(RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/{playerId:guid}", AddAsync);
        routes.MapDelete("/{playerId:guid}", RemoveAsync);
    }

    private static async Task<IResult> AddAsync(
        Guid playerId,
        HttpContext context,
        ITenantDb database,
        ILocks locks,
        CancellationToken cancellationToken)
    {
        if (!TryGetPlayer(context.User, out var actorId, out var tenantId)) return Results.Unauthorized();
        if (actorId == playerId) return Results.BadRequest(new { error = "self" });

        var targetIsPlayer = await database.InTenantAsync(tenantId, async transaction =>
        {
            await locks.PlayersAsync(transaction, [actorId, playerId], cancellationToken);
            var isPlayer = await transaction.QuerySingleOrDefaultAsync<bool?>("""
                select account_id is not null
                from players
                where id = @playerId and deleted_at is null
                """, new { playerId }, cancellationToken);
            if (isPlayer is not true) return false;

            await transaction.ExecuteAsync("""
                insert into friendships (tenant_id, player_id, friend_id)
                values (@tenantId, @actorId, @playerId),
                       (@tenantId, @playerId, @actorId)
                on conflict do nothing
                """, new { tenantId, actorId, playerId }, cancellationToken);
            return true;
        }, cancellationToken: cancellationToken);

        return targetIsPlayer ? Results.Ok() : Results.NotFound();
    }

    private static async Task<IResult> RemoveAsync(
        Guid playerId,
        HttpContext context,
        ITenantDb database,
        ILocks locks,
        CancellationToken cancellationToken)
    {
        if (!TryGetPlayer(context.User, out var actorId, out var tenantId)) return Results.Unauthorized();

        await database.InTenantAsync(tenantId, async transaction =>
        {
            await locks.PlayersAsync(transaction, [actorId, playerId], cancellationToken);
            await transaction.ExecuteAsync("""
                delete from friendships
                where (player_id = @actorId and friend_id = @playerId)
                   or (player_id = @playerId and friend_id = @actorId)
                """, new { actorId, playerId }, cancellationToken);
            return 0;
        }, cancellationToken: cancellationToken);

        return Results.Ok();
    }

    private static Task CleanupPlayerAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("""
            delete from friendships
            where player_id = @playerId or friend_id = @playerId
            """, new { playerId }, cancellationToken);

    private static bool TryGetPlayer(ClaimsPrincipal user, out Guid playerId, out Guid tenantId)
    {
        playerId = Guid.Empty;
        tenantId = Guid.Empty;
        return Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out playerId)
            && Guid.TryParse(user.FindFirstValue("tenant"), out tenantId);
    }
}

public sealed record FriendsSettings;
