using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Quesshi.Application.Content;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Editor administration routes for tenant-owned packs (<c>owner_id</c> null); player-owned packs are never reachable here.</summary>
public sealed class PackAdminEndpoints : ICoreRouteModule
{
    private static readonly PackActor Editor = new(null, IsTenantAdmin: true);

    public void Map(CoreRouteBuilder routes)
    {
        var packs = routes.AdminRoutes(AdminRole.Editor).MapGroup("/packs");
        packs.MapGet("", ListAsync);
        packs.MapGet("/{id:guid}", GetAsync);
        packs.MapPost("", CreateAsync).WithMetadata(new AdminAuditMetadata("admin.pack.create", "pack"));
        packs.MapPut("/{id:guid}", UpdateAsync).WithMetadata(new AdminAuditMetadata("admin.pack.update", "pack"));
        packs.MapPut("/{id:guid}/visibility", SetVisibilityAsync).WithMetadata(new AdminAuditMetadata("admin.pack.visibility", "pack"));
        packs.MapDelete("/{id:guid}", DeleteAsync).WithMetadata(new AdminAuditMetadata("admin.pack.delete", "pack"));
        packs.MapPost("/{id:guid}/questions", AddQuestionAsync).WithMetadata(new AdminAuditMetadata("admin.pack.question.add", "pack"));
        packs.MapPut("/{id:guid}/questions/order", ReorderAsync).WithMetadata(new AdminAuditMetadata("admin.pack.question.reorder", "pack"));
        packs.MapDelete("/{id:guid}/questions/{questionId:guid}", RemoveQuestionAsync).WithMetadata(new AdminAuditMetadata("admin.pack.question.remove", "pack"));
    }

    private static async Task<IResult> ListAsync(HttpContext context, [FromServices] ITenantDb database, [FromServices] IPacks packs, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var list = await database.InTenantAsync(tenant.Id, transaction => packs.ListTenantOwnedAsync(transaction, cancellationToken), cancellationToken: cancellationToken, readOnly: true);
        return Results.Ok(list.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> GetAsync(HttpContext context, Guid id, [FromServices] ITenantDb database, [FromServices] IPacks packs, [FromServices] IContent content, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var detail = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var pack = await packs.GetAsync(transaction, id, cancellationToken);
            if (pack is null || !PackAccess.CanEdit(pack, Editor)) return null;
            var items = new List<PackQuestionResponse>();
            foreach (var item in await packs.ListQuestionsAsync(transaction, id, cancellationToken))
                if (await content.GetAsync(transaction, item.QuestionId, cancellationToken) is { } question)
                    items.Add(ToResponse(item.Position, question));
            return new PackDetailResponse(ToResponse(pack), items);
        }, cancellationToken: cancellationToken, readOnly: true);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    private static async Task<IResult> CreateAsync(HttpContext context, PackCreateRequest? request, [FromServices] ITenantDb database, [FromServices] IPacks packs, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        PackVisibility visibility = default;
        if (request is null || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Language)
            || (request.Visibility is not null && !PackVisibilities.TryParse(request.Visibility, out visibility)))
            return Error("invalid_pack");
        var pack = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var created = await packs.CreateAsync(transaction, null, request.Title, request.Description, request.Language, cancellationToken);
            if (request.Visibility is null) return created;
            await packs.SetVisibilityAsync(transaction, created.Id, visibility, cancellationToken);
            return created with { Visibility = visibility };
        }, cancellationToken: cancellationToken);
        return Results.Created($"/api/admin/packs/{pack.Id}", ToResponse(pack));
    }

    private static async Task<IResult> UpdateAsync(HttpContext context, Guid id, PackUpdateRequest? request, [FromServices] ITenantDb database, [FromServices] IPacks packs, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (request is null || string.IsNullOrWhiteSpace(request.Title)) return Error("invalid_pack");
        var pack = await EditAsync(database, tenant.Id, packs, id, async transaction =>
        {
            await packs.RenameAsync(transaction, id, request.Title, cancellationToken);
            await packs.DescribeAsync(transaction, id, request.Description, cancellationToken);
            return true;
        }, cancellationToken);
        return pack is null ? Results.NotFound() : Results.Ok(ToResponse(pack));
    }

    private static async Task<IResult> SetVisibilityAsync(HttpContext context, Guid id, PackVisibilityRequest? request, [FromServices] ITenantDb database, [FromServices] IPacks packs, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (!PackVisibilities.TryParse(request?.Visibility, out var visibility)) return Error("invalid_visibility");
        var pack = await EditAsync(database, tenant.Id, packs, id, transaction => packs.SetVisibilityAsync(transaction, id, visibility, cancellationToken), cancellationToken);
        return pack is null ? Results.NotFound() : Results.Ok(ToResponse(pack));
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, Guid id, [FromServices] ITenantDb database, [FromServices] IPacks packs, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var deleted = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            if (!await IsEditableAsync(transaction, packs, id, cancellationToken)) return false;
            var questionIds = (await packs.ListQuestionsAsync(transaction, id, cancellationToken)).Select(item => item.QuestionId).ToArray();
            if (!await packs.DeleteAsync(transaction, id, cancellationToken)) return false;
            await PruneUnusedPackQuestionsAsync(transaction, questionIds, cancellationToken);
            return true;
        }, cancellationToken: cancellationToken);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> AddQuestionAsync(HttpContext context, Guid id, PackQuestionRequest? request, [FromServices] ITenantDb database, [FromServices] IPacks packs, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (request is null || request.QuestionId == Guid.Empty) return Error("invalid_question");
        var outcome = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            if (!await IsEditableAsync(transaction, packs, id, cancellationToken)) return StatusCodes.Status404NotFound;
            return await packs.AddQuestionAsync(transaction, id, request.QuestionId, cancellationToken) ? StatusCodes.Status204NoContent : StatusCodes.Status409Conflict;
        }, cancellationToken: cancellationToken);
        return outcome switch
        {
            StatusCodes.Status204NoContent => Results.NoContent(),
            StatusCodes.Status404NotFound => Results.NotFound(),
            _ => Error("question_not_addable", StatusCodes.Status409Conflict),
        };
    }

    private static async Task<IResult> RemoveQuestionAsync(HttpContext context, Guid id, Guid questionId, [FromServices] ITenantDb database, [FromServices] IPacks packs, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var removed = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            if (!await IsEditableAsync(transaction, packs, id, cancellationToken) || !await packs.RemoveQuestionAsync(transaction, id, questionId, cancellationToken)) return false;
            await PruneUnusedPackQuestionsAsync(transaction, [questionId], cancellationToken);
            return true;
        }, cancellationToken: cancellationToken);
        return removed ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ReorderAsync(HttpContext context, Guid id, PackOrderRequest? request, [FromServices] ITenantDb database, [FromServices] IPacks packs, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (request?.Ids is null) return Error("invalid_order");
        var outcome = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            if (!await IsEditableAsync(transaction, packs, id, cancellationToken)) return StatusCodes.Status404NotFound;
            return await packs.ReorderAsync(transaction, id, request.Ids, cancellationToken) ? StatusCodes.Status204NoContent : StatusCodes.Status400BadRequest;
        }, cancellationToken: cancellationToken);
        return outcome switch
        {
            StatusCodes.Status204NoContent => Results.NoContent(),
            StatusCodes.Status404NotFound => Results.NotFound(),
            _ => Error("invalid_order"),
        };
    }

    /// <summary>Runs <paramref name="change"/> on a pack the editor may edit and returns the pack as it is afterwards; null when the pack is missing, player-owned, or the change did not apply.</summary>
    private static Task<Pack?> EditAsync(ITenantDb database, Guid tenantId, IPacks packs, Guid id, Func<ITenantTx, Task<bool>> change, CancellationToken cancellationToken) =>
        database.InTenantAsync(tenantId, async transaction =>
            await IsEditableAsync(transaction, packs, id, cancellationToken) && await change(transaction)
                ? await packs.GetAsync(transaction, id, cancellationToken)
                : null,
            cancellationToken: cancellationToken);

    private static async Task<bool> IsEditableAsync(ITenantTx transaction, IPacks packs, Guid id, CancellationToken cancellationToken) =>
        await packs.GetAsync(transaction, id, cancellationToken) is { } pack && PackAccess.CanEdit(pack, Editor);

    /// <summary>A question written for a pack has no other home, so it goes when its last pack lets it go; bank questions are never touched.</summary>
    private static Task PruneUnusedPackQuestionsAsync(ITenantTx transaction, Guid[] questionIds, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("""
            delete from questions
            where id = any(@questionIds) and source = 'pack'
              and not exists (select 1 from pack_items where pack_items.question_id = questions.id)
            """, new { questionIds }, cancellationToken);

    private static IResult Error(string error, int statusCode = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error }, statusCode: statusCode);

    private static PackResponse ToResponse(Pack pack) => new(pack.Id, pack.Title, pack.Description, pack.Language, PackVisibilities.ToValue(pack.Visibility), pack.CreatedAt, pack.UpdatedAt);

    private static PackQuestionResponse ToResponse(int position, QuestionEnvelope question) => new(position, question.Id, question.Kind, question.KindVersion, question.Family, question.Language, question.CategoryId, question.Level, question.Prompt, question.Payload, question.Explanation, question.Status.ToString().ToLowerInvariant(), question.MediaId);

    private sealed record PackCreateRequest(string? Title, string? Description, string? Language, string? Visibility);
    private sealed record PackUpdateRequest(string? Title, string? Description);
    private sealed record PackVisibilityRequest(string? Visibility);
    private sealed record PackQuestionRequest(Guid QuestionId);
    private sealed record PackOrderRequest(IReadOnlyList<Guid>? Ids);
    private sealed record PackResponse(Guid Id, string Title, string? Description, string Language, string Visibility, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record PackQuestionResponse(int Position, Guid Id, string Kind, int KindVersion, string Family, string Language, Guid? CategoryId, int? Level, string Prompt, JsonElement Payload, string? Explanation, string Status, Guid? MediaId);
    private sealed record PackDetailResponse(PackResponse Pack, IReadOnlyList<PackQuestionResponse> Questions);
}
