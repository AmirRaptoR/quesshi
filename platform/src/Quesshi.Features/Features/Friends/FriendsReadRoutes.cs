using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Features.Friends;

internal static class FriendsReadRoutes
{
    private const int MinimumSearchLength = 2;
    private const int MaxSearchResults = 20;
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    public static void MapPlayerRoutes(RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("", ListAsync);
        routes.MapGet("/search", SearchAsync);
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

    private static async Task<IResult> SearchAsync(
        string? q,
        HttpContext context,
        ITenantRequestContext tenants,
        ITenantDb database,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var playerId)
            || tenants.GetTenantId(context) is not { } tenantId)
        {
            return Results.Unauthorized();
        }

        var text = q?.Trim() ?? "";
        if (text.Length < MinimumSearchLength) return Results.Ok(Array.Empty<FriendSearchCandidate>());

        var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var candidates = await database.InTenantAsync(
            tenantId,
            transaction => transaction.QueryAsync<FriendSearchCandidate>("""
                select candidate.id as Id,
                       candidate.display_name as DisplayName,
                       candidate.avatar_seed as AvatarSeed
                from players candidate
                join accounts account on account.id = candidate.account_id
                where candidate.tenant_id = @tenantId
                  and candidate.id <> @playerId
                  and candidate.banned_at is null
                  and candidate.deleted_at is null
                  and (candidate.display_name ilike @pattern escape '\'
                       or account.email::text ilike @pattern escape '\')
                  and not exists (
                      select 1
                      from friendships friendship
                      where friendship.player_id = @playerId and friendship.friend_id = candidate.id)
                order by candidate.display_name, candidate.id
                limit @maxResults
                """, new { tenantId, playerId, pattern, maxResults = MaxSearchResults }, cancellationToken),
            cancellationToken: cancellationToken,
            readOnly: true);
        return Results.Ok(candidates);
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

public sealed record FriendSearchCandidate(Guid Id, string DisplayName, string AvatarSeed);
