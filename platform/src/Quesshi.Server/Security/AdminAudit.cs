using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Quesshi.Plugin.Api;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Security;

/// <summary>Writes and reads the tenant-scoped audit trail for protected administration routes.</summary>
internal sealed class AdminAuditLog(ITenantDb database)
{
    private const int MaximumPageSize = 100;

    public Task WriteRequestAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        var admin = context.Admin();
        if (tenant is null || admin is null) return Task.CompletedTask;

        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path.Value ?? "/";
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<AdminAuditMetadata>();
        var targetId = context.Request.RouteValues.TryGetValue("id", out var routeId) && Guid.TryParse(routeId?.ToString(), out var parsedId)
            ? parsedId
            : (Guid?)null;
        var data = new AdminAuditData(context.Request.Method, route);
        return database.InTenantAsync(tenant.Id, transaction => transaction.ExecuteAsync("""
            insert into audit_log (tenant_id, actor_type, actor_id, action, target_type, target_id, data)
            values (@tenantId, 'admin', @actorId, @action, @targetType, @targetId, @data)
            """, new
        {
            tenantId = tenant.Id,
            actorId = admin.AdminId,
            action = metadata?.Action ?? $"admin.{context.Request.Method.ToLowerInvariant()}",
            targetType = metadata?.TargetType ?? "route",
            targetId,
            data = new Jsonb<AdminAuditData>(data, AdminAuditJsonContext.Default.AdminAuditData),
        }, cancellationToken), cancellationToken: cancellationToken);
    }

    public async Task<AdminAuditPage> ReadAsync(Guid tenantId, long? before, int? take, CancellationToken cancellationToken)
    {
        if (before is <= 0) throw new ArgumentOutOfRangeException(nameof(before));
        if (take is <= 0 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(take));
        var pageSize = take ?? 50;
        return await database.InTenantAsync(tenantId, async transaction =>
        {
            var rows = await transaction.QueryAsync<AdminAuditEntry>("""
                select id as Id, actor_type as ActorType, actor_id as ActorId, action as Action,
                       target_type as TargetType, target_id as TargetId, data as Data, at as At
                from audit_log
                where (@before is null or id < @before)
                order by id desc
                limit @limit
                """, new { before, limit = pageSize + 1 }, cancellationToken);
            var entries = rows.Take(pageSize).ToArray();
            return new AdminAuditPage(entries, rows.Count > pageSize ? entries[^1].Id : null);
        }, cancellationToken: cancellationToken, readOnly: true);
    }
}

/// <summary>Maps the owner-only audit endpoint through the common administration boundary.</summary>
public sealed class AdminAuditEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes) => routes.AdminRoutes(AdminRole.Owner).MapGet("/audit", GetAsync);

    private static async Task<IResult> GetAsync(HttpContext context, AdminAuditLog audit, long? before, int? take, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();

        try
        {
            return Results.Json(await audit.ReadAsync(tenant.Id, before, take, cancellationToken));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Results.BadRequest(new { error = "invalid_page" });
        }
    }
}

internal sealed record AdminAuditEntry(
    long Id,
    string ActorType,
    Guid? ActorId,
    string Action,
    string TargetType,
    Guid? TargetId,
    JsonElement Data,
    DateTimeOffset At);

internal sealed record AdminAuditPage(IReadOnlyList<AdminAuditEntry> Items, long? Next);

internal sealed record AdminAuditData(string Method, string Route);

/// <summary>Describes the semantic audit action for a protected administration endpoint.</summary>
internal sealed record AdminAuditMetadata(string Action, string TargetType);

[JsonSerializable(typeof(AdminAuditData))]
internal partial class AdminAuditJsonContext : JsonSerializerContext;
