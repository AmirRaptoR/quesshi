using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Tenant moderator operations on active sessions of every format (architecture 7.5).</summary>
public sealed class SessionOperationsEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        // Moderator-and-owner only: an editor outranks a moderator in the role order but has no moderation duties.
        var sessions = routes.PlayerOperationRoutes().MapGroup("/session-operations");
        sessions.MapGet("", ListAsync);
        sessions.MapPost("/{id:guid}/end", EndAsync)
            .WithMetadata(new AdminAuditMetadata("admin.session.end", "session"));
    }

    private static async Task<IResult> ListAsync(HttpContext context, ITenantDb database, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();

        var sessions = await database.InTenantAsync(tenant.Id, tx => tx.QueryAsync<ActiveSessionResponse>("""
            select s.id as Id, s.code as Code, s.format as Format, s.status as Status,
                   s.created_at as CreatedAt,
                   (select count(*) from session_participants p
                     where p.tenant_id = s.tenant_id and p.session_id = s.id and p.status = 'active') as Players
            from sessions s
            where s.tenant_id = @tenantId and s.status not in ('finished', 'cancelled')
            order by s.created_at, s.id
            """, new { tenantId = tenant.Id }, cancellationToken: cancellationToken), cancellationToken: cancellationToken, readOnly: true);
        return Results.Ok(sessions);
    }

    private static async Task<IResult> EndAsync(
        HttpContext context,
        Guid id,
        SessionStateStore store,
        ITenantDb database,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();

        var ended = await store.EndCoreAsync(tenant.Id, id, CoreEndReason.Admin, cancellationToken);
        if (ended) return Results.Ok(new EndSessionResponse(true));

        var exists = await database.InTenantAsync(tenant.Id, tx => tx.QuerySingleOrDefaultAsync<bool>(
            "select exists (select 1 from sessions where tenant_id = @tenantId and id = @id)",
            new { tenantId = tenant.Id, id }, cancellationToken), cancellationToken: cancellationToken, readOnly: true);
        return exists ? Results.Ok(new EndSessionResponse(false)) : Results.NotFound();
    }

    private sealed record ActiveSessionResponse(Guid Id, string Code, string Format, string Status, DateTimeOffset CreatedAt, long Players);
    private sealed record EndSessionResponse(bool Ended);
}
