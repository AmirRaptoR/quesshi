using Microsoft.AspNetCore.Mvc;
using Quesshi.Application.Content;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Tenant category administration and player-facing category display routes.</summary>
public sealed class CategoryEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        routes.OpenRoutes.MapGet("/categories", ListActiveAsync);

        var categories = routes.AdminRoutes(AdminRole.Editor).MapGroup("/categories");
        categories.MapGet("", ListAsync);
        categories.MapPost("", CreateAsync).WithMetadata(new AdminAuditMetadata("admin.category.create", "category"));
        categories.MapPut("/{id:guid}", UpdateAsync).WithMetadata(new AdminAuditMetadata("admin.category.update", "category"));
        categories.MapPut("/reorder", ReorderAsync).WithMetadata(new AdminAuditMetadata("admin.category.reorder", "category"));
        categories.MapPost("/{id:guid}/deactivate", DeactivateAsync).WithMetadata(new AdminAuditMetadata("admin.category.deactivate", "category"));
        categories.MapDelete("/{id:guid}", DeleteAsync).WithMetadata(new AdminAuditMetadata("admin.category.delete", "category"));
    }

    private static async Task<IResult> ListActiveAsync(
        HttpContext context,
        [FromQuery] string? language,
        [FromServices] ICategoryService categories,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        return tenant is null
            ? Results.NotFound()
            : Results.Ok(await categories.ListActiveAsync(tenant.Id, language, cancellationToken));
    }

    private static async Task<IResult> ListAsync(HttpContext context, [FromServices] ICategoryService categories, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        return tenant is null ? Results.NotFound() : Results.Ok(await categories.ListAsync(tenant.Id, cancellationToken));
    }

    private static async Task<IResult> CreateAsync(HttpContext context, CategoryWriteRequest request, [FromServices] ICategoryService categories, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var result = await categories.CreateAsync(tenant.Id, request.ToWrite(), cancellationToken);
        return Answer(result, created: true);
    }

    private static async Task<IResult> UpdateAsync(HttpContext context, Guid id, CategoryWriteRequest request, [FromServices] ICategoryService categories, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        return Answer(await categories.UpdateAsync(tenant.Id, id, request.ToWrite(), cancellationToken));
    }

    private static async Task<IResult> ReorderAsync(HttpContext context, CategoryOrderRequest request, [FromServices] ICategoryService categories, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        return Answer(await categories.ReorderAsync(tenant.Id, request.Ids, cancellationToken));
    }

    private static async Task<IResult> DeactivateAsync(HttpContext context, Guid id, [FromServices] ICategoryService categories, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        return Answer(await categories.DeactivateAsync(tenant.Id, id, cancellationToken));
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, Guid id, [FromServices] ICategoryService categories, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        return Answer(await categories.DeleteAsync(tenant.Id, id, cancellationToken));
    }

    private static IResult Answer(CategoryChangeResult result, bool created = false) => result.Status switch
    {
        CategoryChangeStatus.Success when created => Results.Created($"/api/admin/categories/{result.Category!.Id}", result.Category),
        CategoryChangeStatus.Success when result.Category is not null => Results.Ok(result.Category),
        CategoryChangeStatus.Success => Results.NoContent(),
        CategoryChangeStatus.Invalid => Error("invalid_category", StatusCodes.Status400BadRequest),
        CategoryChangeStatus.DuplicateSlug => Error("slug_taken", StatusCodes.Status409Conflict),
        CategoryChangeStatus.InUse => Error("category_in_use", StatusCodes.Status400BadRequest),
        CategoryChangeStatus.Missing => Results.NotFound(),
        _ => Error("category_change_failed", StatusCodes.Status400BadRequest),
    };

    private static IResult Error(string error, int statusCode) => Results.Json(new { error }, statusCode: statusCode);

    private sealed record CategoryWriteRequest(string? Slug, Dictionary<string, string>? Names, string? Icon, string? Color, int SortOrder, bool? IsActive)
    {
        public CategoryWrite ToWrite() => new(Slug, Names, Icon, Color, SortOrder, IsActive);
    }

    private sealed record CategoryOrderRequest(Guid[]? Ids);
}
