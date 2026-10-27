using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Features.Friends;

internal static class FriendsReadRoutes
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    public static void MapPlayerRoutes(RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("", ListAsync);
    }

    public static async Task<IReadOnlyList<Guid>> GetFriendIdsAsync(
        ITenantTx transaction,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var friends = await ReadFriendsAsync(transaction, playerId, cancellationToken);
        return friends.Select(static friend => friend.Id).ToArray();
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        ITenantRequestContext tenants,
        ITenantDb database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var playerId)
            || tenants.GetTenantId(context) is not { } tenantId)
        {
            return Results.Unauthorized();
        }

        var friends = await database.InTenantAsync(
            tenantId,
            transaction => ReadFriendsAsync(transaction, playerId, cancellationToken),
            cancellationToken: cancellationToken,
            readOnly: true);
        var onlineCutoff = timeProvider.GetUtcNow().Subtract(OnlineWindow);
        return Results.Ok(friends.Select(friend => new FriendListEntry(
            friend.Id,
            friend.DisplayName,
            friend.AvatarSeed,
            friend.LastSeenAt is { } lastSeenAt && lastSeenAt >= onlineCutoff)).ToArray());
    }

    private static async Task<IReadOnlyList<FriendRow>> ReadFriendsAsync(
        ITenantTx transaction,
        Guid playerId,
        CancellationToken cancellationToken) => await transaction.QueryAsync<FriendRow>("""
        select friend.id as Id,
               friend.display_name as DisplayName,
               friend.avatar_seed as AvatarSeed,
               friend.last_seen_at as LastSeenAt
        from friendships friendship
        join players friend on friend.id = friendship.friend_id
        where friendship.player_id = @playerId and friend.deleted_at is null
        order by friend.display_name, friend.id
        """, new { playerId }, cancellationToken);

    private sealed record FriendRow(Guid Id, string DisplayName, string AvatarSeed, DateTimeOffset? LastSeenAt);
}

public sealed record FriendListEntry(Guid Id, string DisplayName, string AvatarSeed, bool IsOnline);
