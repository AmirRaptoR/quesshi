using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Editor administration routes for reusable question-bank content.</summary>
public sealed class QuestionEndpoints : ICoreRouteModule
{
    private const int DefaultPageSize = 25;
    private const int MaximumPageSize = 100;

    public void Map(CoreRouteBuilder routes)
    {
        var questions = routes.AdminRoutes(AdminRole.Editor).MapGroup("/questions");
        questions.MapGet("", ListAsync);
        questions.MapGet("/{id:guid}", GetAsync);
        questions.MapPost("", CreateAsync).WithMetadata(new AdminAuditMetadata("admin.question.create", "question"));
        questions.MapPut("/{id:guid}", UpdateAsync).WithMetadata(new AdminAuditMetadata("admin.question.update", "question"));
        questions.MapDelete("/{id:guid}", DeleteAsync).WithMetadata(new AdminAuditMetadata("admin.question.delete", "question"));
        questions.MapPut("/{id:guid}/status", SetStatusAsync).WithMetadata(new AdminAuditMetadata("admin.question.status", "question"));
        questions.MapPost("/{id:guid}/approve", ApproveAsync).WithMetadata(new AdminAuditMetadata("admin.question.approve", "question"));
        questions.MapPost("/{id:guid}/reject", RejectAsync).WithMetadata(new AdminAuditMetadata("admin.question.reject", "question"));
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        [FromQuery] string? language,
        [FromQuery] string? family,
        [FromQuery] string? kind,
        [FromQuery] string? categoryId,
        [FromQuery] string? level,
        [FromQuery] string? status,
        [FromQuery] string? source,
        [FromQuery] string? text,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] ITenantDb database,
        [FromServices] IContent content,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (!TryFilter(language, family, kind, categoryId, level, status, source, text, out var filter, out var error)) return error!;
        if (page is < 1 || pageSize is < 1 or > MaximumPageSize) return Error("invalid_page", StatusCodes.Status400BadRequest);

        var result = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var questions = await content.ListAsync(transaction, filter, cancellationToken);
            var buckets = await content.BucketCountsAsync(transaction, filter, cancellationToken);
            return (questions, buckets);
        }, cancellationToken: cancellationToken, readOnly: true);
        var selectedPage = page ?? 1;
        var selectedPageSize = pageSize ?? DefaultPageSize;
        var offset = (long)(selectedPage - 1) * selectedPageSize;
        var items = offset >= result.questions.Count
            ? Array.Empty<QuestionResponse>()
            : result.questions.Skip((int)offset).Take(selectedPageSize).Select(ToResponse).ToArray();
        return Results.Ok(new QuestionListResponse(items, result.questions.Count, selectedPage, selectedPageSize, result.buckets.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> GetAsync(HttpContext context, Guid id, [FromServices] ITenantDb database, [FromServices] IContent content, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var question = await database.InTenantAsync(tenant.Id, transaction => content.GetAsync(transaction, id, cancellationToken), cancellationToken: cancellationToken, readOnly: true);
        return question is null ? Results.NotFound() : Results.Ok(ToResponse(question));
    }

    private static async Task<IResult> CreateAsync(HttpContext context, QuestionWriteRequest? request, [FromServices] ITenantDb database, [FromServices] IContent content, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (!TryWrite(request, out var write, out var error)) return error!;
        var result = await database.InTenantAsync(tenant.Id, transaction => content.CreateAsync(transaction, write!, cancellationToken), cancellationToken: cancellationToken);
        return Answer(result, created: true);
    }

    private static async Task<IResult> UpdateAsync(HttpContext context, Guid id, QuestionWriteRequest? request, [FromServices] ITenantDb database, [FromServices] IContent content, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (!TryWrite(request, out var write, out var error)) return error!;
        var result = await database.InTenantAsync(tenant.Id, transaction => content.EditAsync(transaction, id, write!, cancellationToken), cancellationToken: cancellationToken);
        return Answer(result);
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, Guid id, [FromServices] ITenantDb database, [FromServices] IContent content, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var deleted = await database.InTenantAsync(tenant.Id, transaction => content.DeleteAsync(transaction, id, cancellationToken), cancellationToken: cancellationToken);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> SetStatusAsync(HttpContext context, Guid id, QuestionStatusRequest? request, [FromServices] ITenantDb database, [FromServices] IContent content, CancellationToken cancellationToken) =>
        await ChangeStatusAsync(context, id, request?.Status, database, content, cancellationToken);

    private static Task<IResult> ApproveAsync(HttpContext context, Guid id, [FromServices] ITenantDb database, [FromServices] IContent content, CancellationToken cancellationToken) =>
        ChangeStatusAsync(context, id, QuestionStatus.Approved.ToString(), database, content, cancellationToken);

    private static Task<IResult> RejectAsync(HttpContext context, Guid id, [FromServices] ITenantDb database, [FromServices] IContent content, CancellationToken cancellationToken) =>
        ChangeStatusAsync(context, id, QuestionStatus.Rejected.ToString(), database, content, cancellationToken);

    private static async Task<IResult> ChangeStatusAsync(HttpContext context, Guid id, string? value, ITenantDb database, IContent content, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (!TryEnum(value, out QuestionStatus status)) return Error("invalid_status", StatusCodes.Status400BadRequest);
        var changed = await database.InTenantAsync(tenant.Id, transaction => content.SetStatusAsync(transaction, id, status, cancellationToken), cancellationToken: cancellationToken);
        return changed ? Results.NoContent() : Results.NotFound();
    }

    private static bool TryFilter(string? language, string? family, string? kind, string? categoryId, string? level, string? status, string? source, string? text, out QuestionFilter filter, out IResult? error)
    {
        filter = default!;
        error = null;
        if (!TryGuid(categoryId, out var category) || !TryInteger(level, out var questionLevel) || !TryEnum(status, out QuestionStatus? questionStatus) || !TryEnum(source, out QuestionSource? questionSource))
        {
            error = Error("invalid_filter", StatusCodes.Status400BadRequest);
            return false;
        }

        filter = new(language, family, kind, category, questionLevel, questionStatus, questionSource, text);
        return true;
    }

    private static bool TryWrite(QuestionWriteRequest? request, out QuestionWrite? write, out IResult? error)
    {
        write = null;
        error = null;
        if (request is null || !TryEnum(request.Status, out QuestionStatus status) || !TryEnum(request.Source, out QuestionSource source))
        {
            error = Error("invalid_question", StatusCodes.Status400BadRequest, [new("question.invalid", "The question envelope has an invalid status or source.")]);
            return false;
        }

        write = new(request.Kind, request.KindVersion, request.Family, request.Language, request.CategoryId, request.Level, request.Prompt, request.Payload, request.Explanation, request.Topic, status, source, request.OwnerId);
        return true;
    }

    private static bool TryGuid(string? value, out Guid? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Guid.TryParse(value, out var parsed)) return false;
        id = parsed;
        return true;
    }

    private static bool TryInteger(string? value, out int? number)
    {
        number = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!int.TryParse(value, out var parsed)) return false;
        number = parsed;
        return true;
    }

    private static bool TryEnum<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out result) && Enum.IsDefined(result);
    }

    private static bool TryEnum<T>(string? value, out T? result) where T : struct, Enum
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!TryEnum(value, out T parsed)) return false;
        result = parsed;
        return true;
    }

    private static IResult Answer(QuestionWriteResult result, bool created = false) => result.Status switch
    {
        QuestionWriteStatus.Success when created => Results.Created($"/api/admin/questions/{result.Question!.Id}", ToResponse(result.Question)),
        QuestionWriteStatus.Success => Results.Ok(ToResponse(result.Question!)),
        QuestionWriteStatus.Missing => Results.NotFound(),
        QuestionWriteStatus.Invalid => Error("invalid_question", StatusCodes.Status400BadRequest, result.Problems),
        QuestionWriteStatus.Duplicate => Error("duplicate_question", StatusCodes.Status409Conflict),
        QuestionWriteStatus.Oversize => Error("question_too_large", StatusCodes.Status400BadRequest, [new("question.oversize", "The question card and reveal are too large.")]),
        _ => Error("question_change_failed", StatusCodes.Status400BadRequest),
    };

    private static IResult Error(string error, int statusCode, IReadOnlyList<ContentProblem>? problems = null) =>
        Results.Json(problems is null ? new { error } : new { error, problems }, statusCode: statusCode);

    private static QuestionResponse ToResponse(QuestionEnvelope question) => new(question.Id, question.Kind, question.KindVersion, question.Family, question.Language, question.CategoryId, question.Level, question.Prompt, question.Payload, question.Explanation, question.Topic, question.Status.ToString().ToLowerInvariant(), question.Source.ToString().ToLowerInvariant(), question.OwnerId);
    private static QuestionBucketResponse ToResponse(QuestionBucketCount bucket) => new(bucket.Language, bucket.CategoryId, bucket.Level, bucket.Kind, bucket.Approved, bucket.Pending);

    private sealed record QuestionWriteRequest(string? Kind, int KindVersion, string? Family, string? Language, Guid? CategoryId, int? Level, string? Prompt, JsonElement Payload, string? Explanation, string? Topic, string? Status, string? Source, Guid? OwnerId);
    private sealed record QuestionStatusRequest(string? Status);
    private sealed record QuestionResponse(Guid Id, string Kind, int KindVersion, string Family, string Language, Guid? CategoryId, int? Level, string Prompt, JsonElement Payload, string? Explanation, string? Topic, string Status, string Source, Guid? OwnerId);
    private sealed record QuestionBucketResponse(string Language, Guid? CategoryId, int? Level, string Kind, int Approved, int Pending);
    private sealed record QuestionListResponse(IReadOnlyList<QuestionResponse> Items, int Total, int Page, int PageSize, IReadOnlyList<QuestionBucketResponse> Buckets);
}
