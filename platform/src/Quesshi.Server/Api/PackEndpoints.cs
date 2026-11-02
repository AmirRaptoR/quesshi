using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Quesshi.Application;
using Quesshi.Application.Packs;
using Quesshi.Application.Profiles;
using Quesshi.Application.Tenancy;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>A signed-in player's own packs and the player-written questions in them. Guests are refused by the player route group.</summary>
public sealed class PackEndpoints : ICoreRouteModule
{
    private const string CustomQuestions = "custom_questions";
    private const int MaximumTitleLength = 120;
    private const int MaximumDescriptionLength = 2000;
    private const int MaximumLanguageLength = 16;

    public void Map(CoreRouteBuilder routes)
    {
        var packs = routes.PlayerRoutes.MapGroup("/packs");
        packs.MapGet("", ListAsync);
        packs.MapPost("", CreateAsync);
        packs.MapGet("/{id:guid}", GetAsync);
        packs.MapPut("/{id:guid}", UpdateAsync);
        packs.MapDelete("/{id:guid}", DeleteAsync);
        packs.MapGet("/{id:guid}/questions", ListQuestionsAsync);
        packs.MapPost("/{id:guid}/questions", AddQuestionAsync);
        packs.MapPut("/{id:guid}/questions/order", ReorderAsync);
        packs.MapPut("/{id:guid}/questions/{questionId:guid}", UpdateQuestionAsync);
        packs.MapDelete("/{id:guid}/questions/{questionId:guid}", RemoveQuestionAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext context, [FromServices] ITenantDb database, [FromServices] IPacks packs, CancellationToken cancellationToken)
    {
        var (tenant, player) = Caller(context);
        var owned = await database.InTenantAsync(tenant.Id, transaction => packs.ListOwnedAsync(transaction, player.PlayerId, cancellationToken), cancellationToken: cancellationToken, readOnly: true);
        return Results.Ok(owned.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        PackWriteRequest? request,
        [FromServices] ITenantDb database,
        [FromServices] IPacks packs,
        [FromServices] ICapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var (tenant, player) = Caller(context);
        if (!await capabilities.IsOnAsync(tenant.Id, CapabilityType.Switch, CustomQuestions, cancellationToken)) return Disabled();
        var title = request?.Title?.Trim();
        var description = Normalize(request?.Description);
        var visibility = PackVisibility.Private;
        if (!ValidTitle(title) || !ValidDescription(description) || (request!.Visibility is not null && !PackVisibilities.TryParse(request.Visibility, out visibility)))
        {
            return Error("invalid_pack", StatusCodes.Status400BadRequest);
        }

        var language = request.Language?.Trim();
        if (language is { Length: 0 or > MaximumLanguageLength }) return Error("invalid_pack", StatusCodes.Status400BadRequest);
        language ??= (await PlayerProfiles.GetAsync(database, tenant.Id, player.PlayerId, cancellationToken))?.Lang ?? "en";
        var pack = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var created = await packs.CreateAsync(transaction, player.PlayerId, title!, description, language, cancellationToken);
            if (visibility != PackVisibility.Private) await packs.SetVisibilityAsync(transaction, created.Id, visibility, cancellationToken);
            return created with { Visibility = visibility };
        }, cancellationToken: cancellationToken);
        return Results.Created($"/api/packs/{pack.Id}", ToResponse(pack));
    }

    private static async Task<IResult> GetAsync(HttpContext context, Guid id, [FromServices] ITenantDb database, [FromServices] IPacks packs, CancellationToken cancellationToken)
    {
        var (tenant, player) = Caller(context);
        var pack = await database.InTenantAsync(tenant.Id, transaction => packs.GetAsync(transaction, id, cancellationToken), cancellationToken: cancellationToken, readOnly: true);
        return pack is null || !PackAccess.CanUse(pack, Actor(player)) ? Results.NotFound() : Results.Ok(ToResponse(pack));
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context,
        Guid id,
        PackWriteRequest? request,
        [FromServices] ITenantDb database,
        [FromServices] IPacks packs,
        [FromServices] ICapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var (tenant, player) = Caller(context);
        if (!await capabilities.IsOnAsync(tenant.Id, CapabilityType.Switch, CustomQuestions, cancellationToken)) return Disabled();
        var title = request?.Title?.Trim();
        var description = Normalize(request?.Description);
        var visibility = default(PackVisibility);
        if (request is null
            || (request.Title is not null && !ValidTitle(title))
            || !ValidDescription(description)
            || (request.Visibility is not null && !PackVisibilities.TryParse(request.Visibility, out visibility)))
        {
            return Error("invalid_pack", StatusCodes.Status400BadRequest);
        }

        return await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var denied = await EditablePackAsync(transaction, packs, id, player, cancellationToken);
            if (denied.Result is not null) return denied.Result;
            if (title is not null) await packs.RenameAsync(transaction, id, title, cancellationToken);
            if (request.Description is not null) await packs.DescribeAsync(transaction, id, description, cancellationToken);
            if (request.Visibility is not null) await packs.SetVisibilityAsync(transaction, id, visibility, cancellationToken);
            return Results.Ok(ToResponse((await packs.GetAsync(transaction, id, cancellationToken))!));
        }, cancellationToken: cancellationToken);
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        Guid id,
        [FromServices] ITenantDb database,
        [FromServices] IPacks packs,
        [FromServices] ICapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var (tenant, player) = Caller(context);
        if (!await capabilities.IsOnAsync(tenant.Id, CapabilityType.Switch, CustomQuestions, cancellationToken)) return Disabled();
        return await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var denied = await EditablePackAsync(transaction, packs, id, player, cancellationToken);
            if (denied.Result is not null) return denied.Result;
            return await packs.DeleteAsync(transaction, id, cancellationToken) ? Results.NoContent() : Results.NotFound();
        }, cancellationToken: cancellationToken);
    }

    private static async Task<IResult> ListQuestionsAsync(
        HttpContext context,
        Guid id,
        [FromServices] ITenantDb database,
        [FromServices] IPacks packs,
        [FromServices] IContent content,
        CancellationToken cancellationToken)
    {
        var (tenant, player) = Caller(context);
        return await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var pack = await packs.GetAsync(transaction, id, cancellationToken);
            if (pack is null || !PackAccess.CanUse(pack, Actor(player))) return Results.NotFound();
            var items = await packs.ListQuestionsAsync(transaction, id, cancellationToken);
            var questions = new List<PackQuestionResponse>(items.Count);
            foreach (var item in items.OrderBy(item => item.Position))
            {
                var question = await content.GetAsync(transaction, item.QuestionId, cancellationToken);
                if (question is not null) questions.Add(ToResponse(item.Position, question));
            }

            return Results.Ok(questions);
        }, cancellationToken: cancellationToken, readOnly: true);
    }

    private static async Task<IResult> AddQuestionAsync(
        HttpContext context,
        Guid id,
        QuestionWriteRequest? request,
        [FromServices] ITenantDb database,
        [FromServices] IPacks packs,
        [FromServices] IContent content,
        [FromServices] IKindRegistry kinds,
        [FromServices] ICapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var (tenant, player) = Caller(context);
        if (!await capabilities.IsOnAsync(tenant.Id, CapabilityType.Switch, CustomQuestions, cancellationToken)) return Disabled();
        return await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var denied = await EditablePackAsync(transaction, packs, id, player, cancellationToken);
            if (denied.Pack is null) return denied.Result!;
            if (!TryWrite(request, denied.Pack, player, kinds, out var write)) return Error("invalid_question", StatusCodes.Status400BadRequest);
            var result = await content.CreateAsync(transaction, write, cancellationToken);
            if (result.Status != QuestionWriteStatus.Success) return Answer(result);
            await packs.AddQuestionAsync(transaction, id, result.Question!.Id, cancellationToken);
            var position = (await packs.ListQuestionsAsync(transaction, id, cancellationToken)).First(item => item.QuestionId == result.Question.Id).Position;
            return Results.Created($"/api/packs/{id}/questions", ToResponse(position, result.Question));
        }, cancellationToken: cancellationToken);
    }

    private static async Task<IResult> UpdateQuestionAsync(
        HttpContext context,
        Guid id,
        Guid questionId,
        QuestionWriteRequest? request,
        [FromServices] ITenantDb database,
        [FromServices] IPacks packs,
        [FromServices] IContent content,
        [FromServices] IKindRegistry kinds,
        [FromServices] ICapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var (tenant, player) = Caller(context);
        if (!await capabilities.IsOnAsync(tenant.Id, CapabilityType.Switch, CustomQuestions, cancellationToken)) return Disabled();
        return await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var denied = await EditablePackAsync(transaction, packs, id, player, cancellationToken);
            if (denied.Pack is null) return denied.Result!;
            var items = await packs.ListQuestionsAsync(transaction, id, cancellationToken);
            var existing = items.Any(item => item.QuestionId == questionId) ? await content.GetAsync(transaction, questionId, cancellationToken) : null;
            if (existing is null || existing.Source != QuestionSource.Player || existing.OwnerId != player.PlayerId) return Results.NotFound();
            if (!TryWrite(request, denied.Pack, player, kinds, out var write)) return Error("invalid_question", StatusCodes.Status400BadRequest);
            var result = await content.EditAsync(transaction, questionId, write, cancellationToken);
            if (result.Status != QuestionWriteStatus.Success) return Answer(result);
            var position = items.First(item => item.QuestionId == questionId).Position;
            return Results.Ok(ToResponse(position, result.Question!));
        }, cancellationToken: cancellationToken);
    }

    private static async Task<IResult> RemoveQuestionAsync(
        HttpContext context,
        Guid id,
        Guid questionId,
        [FromServices] ITenantDb database,
        [FromServices] IPacks packs,
        [FromServices] ICapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var (tenant, player) = Caller(context);
        if (!await capabilities.IsOnAsync(tenant.Id, CapabilityType.Switch, CustomQuestions, cancellationToken)) return Disabled();
        return await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var denied = await EditablePackAsync(transaction, packs, id, player, cancellationToken);
            if (denied.Result is not null) return denied.Result;
            return await packs.RemoveQuestionAsync(transaction, id, questionId, cancellationToken) ? Results.NoContent() : Results.NotFound();
        }, cancellationToken: cancellationToken);
    }

    private static async Task<IResult> ReorderAsync(
        HttpContext context,
        Guid id,
        ReorderRequest? request,
        [FromServices] ITenantDb database,
        [FromServices] IPacks packs,
        [FromServices] ICapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var (tenant, player) = Caller(context);
        if (!await capabilities.IsOnAsync(tenant.Id, CapabilityType.Switch, CustomQuestions, cancellationToken)) return Disabled();
        if (request?.QuestionIds is not { } ids) return Error("invalid_order", StatusCodes.Status400BadRequest);
        return await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var denied = await EditablePackAsync(transaction, packs, id, player, cancellationToken);
            if (denied.Result is not null) return denied.Result;
            return await packs.ReorderAsync(transaction, id, ids, cancellationToken) ? Results.NoContent() : Error("invalid_order", StatusCodes.Status400BadRequest);
        }, cancellationToken: cancellationToken);
    }

    /// <summary>Loads the pack and applies <see cref="PackAccess.CanEdit"/>: a pack the caller cannot even use is hidden, one they can use but not edit is forbidden.</summary>
    private static async Task<(Pack? Pack, IResult? Result)> EditablePackAsync(ITenantTx transaction, IPacks packs, Guid id, PlayerIdentity player, CancellationToken cancellationToken)
    {
        var pack = await packs.GetAsync(transaction, id, cancellationToken);
        var actor = Actor(player);
        if (pack is null || !PackAccess.CanUse(pack, actor)) return (null, Results.NotFound());
        if (!PackAccess.CanEdit(pack, actor)) return (null, Error("forbidden", StatusCodes.Status403Forbidden));
        return (pack, null);
    }

    private static bool TryWrite(QuestionWriteRequest? request, Pack pack, PlayerIdentity player, IKindRegistry kinds, out QuestionWrite write)
    {
        write = default!;
        if (request?.Kind is null || !kinds.TryGetCurrent(request.Kind.Trim(), out var kind) || kind is null) return false;
        write = new(kind.Id, request.KindVersion == 0 ? kind.Version : request.KindVersion, kind.Family, request.Language ?? pack.Language, null, request.Level ?? 1, request.Prompt, request.Payload, request.Explanation, null, QuestionStatus.Approved, QuestionSource.Player, player.PlayerId);
        return true;
    }

    private static IResult Answer(QuestionWriteResult result) => result.Status switch
    {
        QuestionWriteStatus.Missing => Results.NotFound(),
        QuestionWriteStatus.Duplicate => Error("duplicate_question", StatusCodes.Status409Conflict),
        QuestionWriteStatus.Oversize => Error("question_too_large", StatusCodes.Status400BadRequest, result.Problems),
        _ => Error("invalid_question", StatusCodes.Status400BadRequest, result.Problems),
    };

    private static (Tenant Tenant, PlayerIdentity Player) Caller(HttpContext context) =>
        (TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("Pack access requires tenant resolution."),
         context.Player() ?? throw new InvalidOperationException("Pack access requires player authentication."));

    private static PackActor Actor(PlayerIdentity player) => new(player.PlayerId);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool ValidTitle(string? title) => !string.IsNullOrWhiteSpace(title) && title.Length <= MaximumTitleLength;
    private static bool ValidDescription(string? description) => description is null || description.Length <= MaximumDescriptionLength;

    private static IResult Disabled() => Error("custom_questions_disabled", StatusCodes.Status403Forbidden);

    private static IResult Error(string error, int statusCode, IReadOnlyList<ContentProblem>? problems = null) =>
        Results.Json(problems is null ? new { error } : new { error, problems }, statusCode: statusCode);

    private static PackResponse ToResponse(Pack pack) => new(pack.Id, pack.Title, pack.Description, pack.Language, PackVisibilities.ToValue(pack.Visibility), pack.CreatedAt, pack.UpdatedAt);

    private static PackQuestionResponse ToResponse(int position, QuestionEnvelope question) =>
        new(question.Id, position, question.Kind, question.KindVersion, question.Family, question.Language, question.Level, question.Prompt, question.Payload, question.Explanation);

    private sealed record PackWriteRequest(string? Title, string? Description, string? Language, string? Visibility);
    private sealed record QuestionWriteRequest(string? Kind, int KindVersion, string? Language, int? Level, string? Prompt, JsonElement Payload, string? Explanation);
    private sealed record ReorderRequest(IReadOnlyList<Guid>? QuestionIds);
    private sealed record PackResponse(Guid Id, string Title, string? Description, string Language, string Visibility, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record PackQuestionResponse(Guid Id, int Position, string Kind, int KindVersion, string Family, string Language, int? Level, string Prompt, JsonElement Payload, string? Explanation);
}
