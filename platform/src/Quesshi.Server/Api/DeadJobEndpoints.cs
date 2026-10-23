using Microsoft.AspNetCore.Mvc;
using Quesshi.Application.Jobs;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Tenant owner recovery operations for durable jobs that exhausted their attempts.</summary>
public sealed class DeadJobEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        var deadJobs = routes.AdminRoutes(AdminRole.Owner).MapGroup("/dead-jobs");
        deadJobs.MapGet("", ListAsync);
        deadJobs.MapGet("/{id:long}", GetAsync);
        deadJobs.MapPost("/{id:long}/rerun", RerunAsync)
            .WithMetadata(new AdminAuditMetadata("admin.job.rerun", "job"));
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        [FromServices] IJobStore jobs,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();

        var deadJobs = await jobs.ListDeadForTenantAsync(tenant.Id, cancellationToken);
        return Results.Ok(deadJobs.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        long id,
        [FromServices] IJobStore jobs,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();

        var job = await jobs.GetForTenantAsync(tenant.Id, id, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(ToResponse(job));
    }

    private static async Task<IResult> RerunAsync(
        HttpContext context,
        long id,
        [FromServices] IJobStore jobs,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();

        var job = await jobs.RerunDeadForTenantAsync(tenant.Id, id, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(ToResponse(job));
    }

    private static DeadJobResponse ToResponse(TenantJob job) =>
        new(job.Id, job.Kind, job.Attempts, job.LastError, job.OccurredAt, job.Status);

    private sealed record DeadJobResponse(
        long Id,
        string Kind,
        int Attempts,
        string? LastError,
        DateTimeOffset? OccurredAt,
        string Status);
}
