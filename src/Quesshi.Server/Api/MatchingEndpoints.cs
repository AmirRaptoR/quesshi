using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

public static class MatchingEndpoints
{
    private sealed class AllowGuest;
    private const int MaxCodeAttempts = 5;

    public static void MapMatching(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/matching").RequireAuthorization();
        api.AddEndpointFilter(static async (context, next) =>
        {
            var open = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<AllowGuest>() is not null;
            return context.HttpContext.User.IsGuest() && !open
                ? Results.Forbid()
                : await next(context);
        });

        api.MapPost("/lobby", async (CreateMatchingLobbyDto body, HttpContext ctx,
            IGrainFactory grains, IIdFactory ids, IMatchArchive archive, IPlayerRepository players,
            IMatchingCategoryRepository categories) =>
            await CreateAsync(body, ctx.User.PlayerId()!, grains, ids, archive, players, categories));

        api.MapPost("/join/{code}", async (string code, HttpContext ctx, IGrainFactory grains,
            IMatchArchive archive, IPlayerRepository players, IMatchingCategoryRepository categories) =>
            await JoinAsync(code, ctx.User.PlayerId()!, grains, archive, players, categories))
            .WithMetadata(new AllowGuest());

        api.MapGet("/by-code/{code}", async (string code, HttpContext ctx, IGrainFactory grains,
            IMatchArchive archive, IPlayerRepository players) =>
            await ByCodeAsync(code, ctx.User.PlayerId()!, grains, archive, players))
            .WithMetadata(new AllowGuest());

        api.MapGet("/{id}", async (string id, HttpContext ctx, IGrainFactory grains,
            IPlayerRepository players) => await GetAsync(id, ctx.User.PlayerId()!, grains, players))
            .WithMetadata(new AllowGuest());

        api.MapPost("/{id}/start", async (string id, HttpContext ctx, IGrainFactory grains) =>
            await StartAsync(id, ctx.User.PlayerId()!, grains));

        api.MapPost("/{id}/leave", async (string id, HttpContext ctx, IGrainFactory grains) =>
            await grains.GetGrain<IMatchingMatchGrain>(id).LeaveAsync(ctx.User.PlayerId()!)
                ? Results.Ok()
                : Results.BadRequest(new { error = "cannot_leave" }));

        api.MapPut("/{id}/settings", async (string id, UpdateMatchingSettingsDto body, HttpContext ctx,
            IGrainFactory grains, IPlayerRepository players, IMatchingCategoryRepository categories) =>
            await UpdateSettingsAsync(id, body, ctx.User.PlayerId()!, grains, players, categories));

        api.MapPost("/{id}/answer", async (string id, SubmitMatchingAnswerDto body, HttpContext ctx,
            IGrainFactory grains, IPlayerRepository players) =>
            await AnswerAsync(id, body, ctx.User.PlayerId()!, grains, players))
            .WithMetadata(new AllowGuest());
    }

    internal static async Task<IResult> CreateAsync(CreateMatchingLobbyDto body, string meId,
        IGrainFactory grains, IIdFactory ids, IMatchArchive archive, IPlayerRepository players,
        IMatchingCategoryRepository categories)
    {
        var me = await players.GetAsync(meId);
        if (me is null) return Results.Unauthorized();
        if (body.Mode is not null && !body.Mode.Equals("matching", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "mode_immutable" });
        if (body.Levels is { Count: > 0 }) return Results.BadRequest(new { error = "levels_not_allowed" });
        if (body.Capacity is < 2 or > MatchRules.MaxParticipants)
            return Results.BadRequest(new { error = "bad_capacity" });

        var count = body.Questions ?? MatchRules.QuestionsPerMatch;
        if (!MatchRules.IsValidCount(count)) return Results.BadRequest(new { error = "bad_question_count" });
        var categoryIds = body.Categories ?? [];
        if (!await CategoriesExistAsync(categoryIds, categories))
            return Results.BadRequest(new { error = "unknown_category" });

        var lang = string.IsNullOrWhiteSpace(body.Lang) ? me.Lang : body.Lang.ToLanguage();
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var code = ids.NewMatchCode();
            if (await archive.ByCodeAsync(code) is not null) continue;
            var id = ids.NewId();
            var grain = grains.GetGrain<IMatchingMatchGrain>(id);
            try
            {
                var view = await grain.CreateAsync(code, meId, (int)lang, count, categoryIds, body.Capacity);
                return Results.Ok(await ToDtoAsync(view, meId, players));
            }
            catch (InvalidOperationException ex) when (ex.Message == "matching_code_collision")
            {
                // The archive check above is only an optimization; a concurrent trivia/live create
                // can win the unique code index after it. The grain clears its state on this exception,
                // so retrying here cannot leave an orphan matching lobby.
            }
        }
        return Results.Json(new { error = "code_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    internal static async Task<IResult> JoinAsync(string code, string meId, IGrainFactory grains,
        IMatchArchive archive, IPlayerRepository players, IMatchingCategoryRepository categories)
    {
        var found = await archive.ByCodeAsync(code);
        if (found is null) return Results.NotFound(new { error = "no_such_code" });
        if (found.Mode != GameMode.Matching) return Results.BadRequest(new { error = "not_a_matching_code" });

        var grain = grains.GetGrain<IMatchingMatchGrain>(found.Id);
        var result = (MatchingJoinResult)await grain.JoinAsync(meId);
        if (result is not MatchingJoinResult.Joined)
            return result switch
            {
                MatchingJoinResult.AlreadyIn => Results.BadRequest(new { error = "duplicate_join" }),
                MatchingJoinResult.Full => Results.BadRequest(new { error = "full_lobby" }),
                MatchingJoinResult.Started => Results.BadRequest(new { error = "match_started" }),
                _ => Results.NotFound(new { error = "no_such_code" })
            };

        var view = await grain.GetAsync(meId);
        return view is null ? Results.NotFound() : Results.Ok(await ToDtoAsync(view, meId, players));
    }

    internal static async Task<IResult> ByCodeAsync(string code, string meId, IGrainFactory grains,
        IMatchArchive archive, IPlayerRepository players)
    {
        var found = await archive.ByCodeAsync(code);
        if (found is null) return Results.NotFound(new { error = "no_such_code" });
        if (found.Mode != GameMode.Matching) return Results.BadRequest(new { error = "not_a_matching_code" });
        return await GetAsync(found.Id, meId, grains, players);
    }

    internal static async Task<IResult> GetAsync(string id, string meId, IGrainFactory grains,
        IPlayerRepository players)
    {
        var view = await grains.GetGrain<IMatchingMatchGrain>(id).GetAsync(meId);
        return view is null ? Results.NotFound() : Results.Ok(await ToDtoAsync(view, meId, players));
    }

    internal static async Task<IResult> StartAsync(string id, string meId, IGrainFactory grains)
    {
        try
        {
            var ok = await grains.GetGrain<IMatchingMatchGrain>(id).StartAsync(meId);
            return ok ? Results.Ok() : Results.BadRequest(new { error = "cannot_start" });
        }
        catch (NotEnoughQuestionsException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("matching_not_enough_questions:", StringComparison.Ordinal))
        {
            return Results.Problem(ex.Message["matching_not_enough_questions:".Length..],
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static async Task<IResult> UpdateSettingsAsync(string id, UpdateMatchingSettingsDto body,
        string meId, IGrainFactory grains, IPlayerRepository players, IMatchingCategoryRepository categories)
    {
        var me = await players.GetAsync(meId);
        if (me is null) return Results.Unauthorized();
        if (body.Mode is not null && !body.Mode.Equals("matching", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "mode_immutable" });
        if (body.Levels is { Count: > 0 }) return Results.BadRequest(new { error = "levels_not_allowed" });
        var count = body.Questions ?? MatchRules.QuestionsPerMatch;
        if (!MatchRules.IsValidCount(count)) return Results.BadRequest(new { error = "bad_question_count" });
        var categoryIds = body.Categories ?? [];
        if (!await CategoriesExistAsync(categoryIds, categories))
            return Results.BadRequest(new { error = "unknown_category" });
        var lang = string.IsNullOrWhiteSpace(body.Lang) ? me.Lang : body.Lang.ToLanguage();
        var ok = await grains.GetGrain<IMatchingMatchGrain>(id).UpdateSettingsAsync(meId, (int)lang,
            count, categoryIds, [], body.Capacity, (int)GameMode.Matching);
        return ok ? Results.Ok() : Results.BadRequest(new { error = "cannot_update_settings" });
    }

    internal static async Task<IResult> AnswerAsync(string id, SubmitMatchingAnswerDto body,
        string meId, IGrainFactory grains, IPlayerRepository players)
    {
        if (!TryParseKind(body.Kind, out var kind)) return Results.BadRequest(new { error = "bad_answer_kind" });
        if (kind == MatchingAnswerKind.SelectedParticipant && body.ParticipantId is null)
            return Results.BadRequest(new { error = "missing_field" });
        if (kind == MatchingAnswerKind.SelectedChoice && body.ChoiceIndex is null)
            return Results.BadRequest(new { error = "missing_field" });
        if (kind == MatchingAnswerKind.NotApplicable && (body.ParticipantId is not null || body.ChoiceIndex is not null))
            return Results.BadRequest(new { error = "contradictory_fields" });
        if (kind == MatchingAnswerKind.SelectedParticipant && body.ChoiceIndex is not null
            || kind == MatchingAnswerKind.SelectedChoice && body.ParticipantId is not null)
            return Results.BadRequest(new { error = "contradictory_fields" });

        try
        {
            var view = await grains.GetGrain<IMatchingMatchGrain>(id).AnswerAsync(meId, body.Slot,
                (int)kind, body.ParticipantId, body.ChoiceIndex);
            return Results.Ok(await ToDtoAsync(view, meId, players));
        }
        catch (InvalidOperationException ex) when (ex.Message == "match_not_found")
        {
            return Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<bool> CategoriesExistAsync(IReadOnlyList<string> ids, IMatchingCategoryRepository categories)
    {
        foreach (var id in ids.Distinct())
            if (await categories.GetAsync(id) is null) return false;
        return true;
    }

    private static bool TryParseKind(string? raw, out MatchingAnswerKind kind)
    {
        kind = raw?.ToLowerInvariant() switch
        {
            "participant" => MatchingAnswerKind.SelectedParticipant,
            "choice" => MatchingAnswerKind.SelectedChoice,
            "na" => MatchingAnswerKind.NotApplicable,
            _ => (MatchingAnswerKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    private static async Task<MatchingViewDto> ToDtoAsync(MatchingView view, string meId, IPlayerRepository players)
    {
        var people = await players.GetManyAsync(view.Participants.Select(p => p.Id).ToList());
        var lookup = people.ToDictionary(p => p.Id);
        var participants = view.Participants.Select(p =>
        {
            var player = lookup.GetValueOrDefault(p.Id);
            return new MatchingParticipantDto(p.Id, player?.DisplayName ?? p.Id, p.Active, player?.IsGuest ?? false);
        }).ToList();

        MatchingSlotDto? Slot(MatchingSlotView? slot)
            => slot is null ? null : new MatchingSlotDto(slot.Slot, slot.QuestionId, slot.Prompt,
                [.. slot.Options.Select(o => new MatchingOptionDto(KindName(o.Kind), o.ParticipantId,
                    o.ParticipantId is null ? null : lookup.GetValueOrDefault(o.ParticipantId)?.DisplayName ?? o.ParticipantId,
                    o.ChoiceIndex, o.Text, o.Kind == (int)MatchingAnswerKind.NotApplicable))],
                slot.ServedAt, slot.AnsweredParticipantIds,
                [.. slot.Answers.Select(a => new MatchingAnswerDto(KindName(a.Kind), a.ParticipantId,
                    a.ChoiceIndex, a.At))]);

        var own = view.OwnAnswer is null ? null : new MatchingAnswerDto(KindName(view.OwnAnswer.Kind),
            view.OwnAnswer.ParticipantId, view.OwnAnswer.ChoiceIndex, view.OwnAnswer.At);
        var results = view.Results is null ? null : new MatchingResultsDto(
            [.. view.Results.Slots.Select(slot => slot is null ? null
                : new MatchingSlotResultDto(slot.Slot, [.. slot.Counts], slot.AllAgreed))],
            view.Results.PairStats is null ? null : [.. view.Results.PairStats.Select(pair =>
                new MatchingPairStatDto(pair.FirstParticipantId, pair.SecondParticipantId, pair.Same,
                    pair.Different, pair.AgreementPercent))],
            view.Results.AllAgreedCount);
        return new MatchingViewDto(view.Id, view.Code, "matching", ((Language)view.Lang).Code(), view.Capacity,
            ((MatchState)view.State).ToString().ToLowerInvariant(), participants, view.CurrentSlotIndex,
            view.TotalSlots, Slot(view.CurrentSlot), Slot(view.LastClosedSlot), own, view.CreatedAt,
            view.EndedAt, results);
    }

    private static string KindName(int kind) => (MatchingAnswerKind)kind switch
    {
        MatchingAnswerKind.SelectedParticipant => "participant",
        MatchingAnswerKind.SelectedChoice => "choice",
        _ => "na"
    };
}
