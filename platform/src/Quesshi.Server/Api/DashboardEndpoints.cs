using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Returns the current tenant's operations dashboard totals to moderators.</summary>
public sealed class DashboardEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes) =>
        routes.AdminRoutes(AdminRole.Moderator).MapGet("/dashboard", GetAsync);

    private static async Task<IResult> GetAsync(HttpContext context, ITenantDb database, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();

        var dashboard = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var players = await transaction.QuerySingleOrDefaultAsync<long>("select count(*) from players", cancellationToken: cancellationToken);
            var sessionStatuses = await transaction.QueryAsync<DashboardCount>("""
                select status as Key, count(*) as Count
                from sessions
                group by status
                order by status
                """, cancellationToken: cancellationToken);
            var sessionFormats = await transaction.QueryAsync<DashboardCount>("""
                select format as Key, count(*) as Count
                from sessions
                group by format
                order by format
                """, cancellationToken: cancellationToken);
            var questionStatuses = await transaction.QueryAsync<DashboardCount>("""
                select status as Key, count(*) as Count
                from questions
                group by status
                order by status
                """, cancellationToken: cancellationToken);

            return new DashboardResponse(players, sessionStatuses, sessionFormats, questionStatuses);
        }, cancellationToken: cancellationToken, readOnly: true);

        return Results.Ok(dashboard);
    }

    private sealed record DashboardResponse(
        long Players,
        IReadOnlyList<DashboardCount> SessionStatuses,
        IReadOnlyList<DashboardCount> SessionFormats,
        IReadOnlyList<DashboardCount> QuestionStatuses);

    private sealed record DashboardCount(string Key, long Count);
}
