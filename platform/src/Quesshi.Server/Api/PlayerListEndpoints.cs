using Microsoft.AspNetCore.Mvc;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Tenant moderator queries for player operations.</summary>
public sealed class PlayerListEndpoints : ICoreRouteModule
{
    private const int DefaultPageSize = 25;
    private const int MaximumPageSize = 100;

    public void Map(CoreRouteBuilder routes) =>
        routes.AdminRoutes(AdminRole.Moderator).MapGet("/players", ListAsync);

    private static async Task<IResult> ListAsync(
        HttpContext context,
        [FromQuery] string? q,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ITenantDb database,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (page is < 1 || pageSize is < 1 or > MaximumPageSize)
            return Results.Json(new { error = "invalid_page" }, statusCode: StatusCodes.Status400BadRequest);

        var selectedPage = page ?? 1;
        var selectedPageSize = pageSize ?? DefaultPageSize;
        var search = q?.Trim() ?? string.Empty;
        var offset = (long)(selectedPage - 1) * selectedPageSize;
        var result = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var parameters = new { tenantId = tenant.Id, search, selectedPageSize, offset };
            var items = await transaction.QueryAsync<PlayerListItem>("""
                select id as Id, display_name as DisplayName, banned_at is not null as IsBanned
                from players
                where tenant_id = @tenantId
                  and deleted_at is null
                  and (@search = '' or display_name ilike '%' || @search || '%')
                order by display_name, id
                limit @selectedPageSize offset @offset
                """, parameters, cancellationToken: cancellationToken);
            var total = await transaction.QuerySingleOrDefaultAsync<long>("""
                select count(*)
                from players
                where tenant_id = @tenantId
                  and deleted_at is null
                  and (@search = '' or display_name ilike '%' || @search || '%')
                """, parameters, cancellationToken: cancellationToken);
            return new PlayerListResponse(items, total, selectedPage, selectedPageSize);
        }, cancellationToken: cancellationToken, readOnly: true);

        return Results.Ok(result);
    }

    private sealed record PlayerListResponse(IReadOnlyList<PlayerListItem> Items, long Total, int Page, int PageSize);
    private sealed record PlayerListItem(Guid Id, string DisplayName, bool IsBanned);
}
