using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

public static class VotingEndpoints
{
    private sealed class AllowGuest;
    private const int MaxCodeAttempts = 5;

    public static void MapVoting(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/voting").RequireAuthorization();
        api.AddEndpointFilter(static async (context, next) =>
        {
            var open = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<AllowGuest>() is not null;
            return context.HttpContext.User.IsGuest() && !open
                ? Results.Forbid()
                : await next(context);
        });

        api.MapPost("/lobby", async (CreateVotingLobbyDto body, HttpContext ctx,
            IGrainFactory grains, IIdFactory ids, IMatchArchive archive, IPlayerRepository players,
            IVotingCategoryRepository categories) =>
            await CreateAsync(body, ctx.User.PlayerId()!, grains, ids, archive, players, categories));

        api.MapPost("/join/{code}", async (string code, HttpContext ctx, IGrainFactory grains,
            IMatchArchive archive, IPlayerRepository players, IVotingCategoryRepository categories) =>
            await JoinAsync(code, ctx.User.PlayerId()!, grains, archive, players, categories))
            .WithMetadata(new AllowGuest());

        api.MapGet("/by-code/{code}", async (string code, HttpContext ctx, IGrainFactory grains,
            IMatchArchive archive, IPlayerRepository players) =>
            await ByCodeAsync(code, ctx.User.PlayerId()!, grains, archive, players))
            .WithMetadata(new AllowGuest());

        api.MapGet("/categories", async (string? lang, IVotingCategoryRepository categories) =>
            (await categories.AllAsync())
                .Where(category => category.IsActive)
                .OrderBy(category => category.SortOrder)
                .ThenBy(category => category.Id)
                .Select(category => category.ToDto(lang.ToLanguage()))
                .ToList())
            .WithMetadata(new AllowGuest());

        api.MapGet("/{id}", async (string id, HttpContext ctx, IGrainFactory grains,
            IPlayerRepository players) => await GetAsync(id, ctx.User.PlayerId()!, grains, players))
            .WithMetadata(new AllowGuest());

        api.MapPost("/{id}/start", async (string id, HttpContext ctx, IGrainFactory grains) =>
            await StartAsync(id, ctx.User.PlayerId()!, grains));

        api.MapPost("/{id}/leave", async (string id, HttpContext ctx, IGrainFactory grains) =>
            await grains.GetTenantGrain<IVotingMatchGrain>(id).LeaveAsync(ctx.User.PlayerId()!)
                ? Results.Ok()
                : Results.BadRequest(new { error = "cannot_leave" }));

        api.MapPut("/{id}/settings", async (string id, UpdateVotingSettingsDto body, HttpContext ctx,
            IGrainFactory grains, IPlayerRepository players, IVotingCategoryRepository categories) =>
            await UpdateSettingsAsync(id, body, ctx.User.PlayerId()!, grains, players, categories));

        api.MapPost("/{id}/answer", async (string id, SubmitVotingAnswerDto body, HttpContext ctx,
            IGrainFactory grains, IPlayerRepository players) =>
            await AnswerAsync(id, body, ctx.User.PlayerId()!, grains, players))
            .WithMetadata(new AllowGuest());
    }

    internal static async Task<IResult> CreateAsync(CreateVotingLobbyDto body, string meId,
        IGrainFactory grains, IIdFactory ids, IMatchArchive archive, IPlayerRepository players,
        IVotingCategoryRepository categories)
    {
        var me = await players.GetAsync(meId);
        if (me is null) return Results.Unauthorized();
        if (body.Mode is not null && !body.Mode.Equals("voting", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "mode_immutable" });
        if (body.Levels is { Count: > 0 }) return Results.BadRequest(new { error = "levels_not_allowed" });
        if (body.Capacity is < 2 or > VotingRules.MaxParticipants)
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
            var grain = grains.GetTenantGrain<IVotingMatchGrain>(id);
            try
            {
                var view = await grain.CreateAsync(code, meId, (int)lang, count, categoryIds, body.Capacity);
                return Results.Ok(await ToDtoAsync(view, meId, players));
            }
            catch (InvalidOperationException ex) when (ex.Message == "voting_code_collision")
            {
                // The archive check above is only an optimization; a concurrent trivia/live create
                // can win the unique code index after it. The grain clears its state on this exception,
                // so retrying here cannot leave an orphan voting lobby.
            }
        }
        return Results.Json(new { error = "code_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    internal static async Task<IResult> JoinAsync(string code, string meId, IGrainFactory grains,
        IMatchArchive archive, IPlayerRepository players, IVotingCategoryRepository categories)
    {
        var found = await archive.ByCodeAsync(code);
        if (found is null) return Results.NotFound(new { error = "no_such_code" });
        if (found.Mode != GameMode.Voting) return Results.BadRequest(new { error = "not_a_voting_code" });

        var grain = grains.GetTenantGrain<IVotingMatchGrain>(found.Id);
        var result = (VotingJoinResult)await grain.JoinAsync(meId);
        if (result is not VotingJoinResult.Joined)
            return result switch
            {
                VotingJoinResult.AlreadyIn => Results.BadRequest(new { error = "duplicate_join" }),
                VotingJoinResult.Full => Results.BadRequest(new { error = "full_lobby" }),
                VotingJoinResult.Started => Results.BadRequest(new { error = "match_started" }),
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
        if (found.Mode != GameMode.Voting) return Results.BadRequest(new { error = "not_a_voting_code" });
        return await GetAsync(found.Id, meId, grains, players);
    }

    internal static async Task<IResult> GetAsync(string id, string meId, IGrainFactory grains,
        IPlayerRepository players)
    {
        var view = await grains.GetTenantGrain<IVotingMatchGrain>(id).GetAsync(meId);
        return view is null ? Results.NotFound() : Results.Ok(await ToDtoAsync(view, meId, players));
    }

    internal static async Task<IResult> StartAsync(string id, string meId, IGrainFactory grains)
    {
        try
        {
            var ok = await grains.GetTenantGrain<IVotingMatchGrain>(id).StartAsync(meId);
            return ok ? Results.Ok() : Results.BadRequest(new { error = "cannot_start" });
        }
        catch (NotEnoughQuestionsException ex)
        {
            return NotEnoughQuestions(ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("voting_not_enough_questions:", StringComparison.Ordinal))
        {
            return NotEnoughQuestions(ex.Message["voting_not_enough_questions:".Length..]);
        }
    }

    // Do not return ProblemDetails here. The player client needs a stable code it can translate,
    // while the detail remains useful to operators inspecting the response or server traffic.
    private static IResult NotEnoughQuestions(string detail)
        => Results.Json(new { error = "not_enough_questions", detail },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    internal static async Task<IResult> UpdateSettingsAsync(string id, UpdateVotingSettingsDto body,
        string meId, IGrainFactory grains, IPlayerRepository players, IVotingCategoryRepository categories)
    {
        var me = await players.GetAsync(meId);
        if (me is null) return Results.Unauthorized();
        if (body.Mode is not null && !body.Mode.Equals("voting", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "mode_immutable" });
        if (body.Levels is { Count: > 0 }) return Results.BadRequest(new { error = "levels_not_allowed" });
        var count = body.Questions ?? MatchRules.QuestionsPerMatch;
        if (!MatchRules.IsValidCount(count)) return Results.BadRequest(new { error = "bad_question_count" });
        var categoryIds = body.Categories ?? [];
        if (!await CategoriesExistAsync(categoryIds, categories))
            return Results.BadRequest(new { error = "unknown_category" });
        var lang = string.IsNullOrWhiteSpace(body.Lang) ? me.Lang : body.Lang.ToLanguage();
        var ok = await grains.GetTenantGrain<IVotingMatchGrain>(id).UpdateSettingsAsync(meId, (int)lang,
            count, categoryIds, [], body.Capacity, (int)GameMode.Voting);
        return ok ? Results.Ok() : Results.BadRequest(new { error = "cannot_update_settings" });
    }

    internal static async Task<IResult> AnswerAsync(string id, SubmitVotingAnswerDto body,
        string meId, IGrainFactory grains, IPlayerRepository players)
    {
        if (!TryParseKind(body.Kind, out var kind)) return Results.BadRequest(new { error = "bad_answer_kind" });
        if (kind == VotingAnswerKind.SelectedParticipant && body.ParticipantId is null)
            return Results.BadRequest(new { error = "missing_field" });
        if (kind == VotingAnswerKind.SelectedChoice && body.ChoiceIndex is null)
            return Results.BadRequest(new { error = "missing_field" });
        if ((kind is VotingAnswerKind.NotApplicable or VotingAnswerKind.MultipleParticipants
                or VotingAnswerKind.NoParticipant)
            && (body.ParticipantId is not null || body.ChoiceIndex is not null))
            return Results.BadRequest(new { error = "contradictory_fields" });
        if (kind == VotingAnswerKind.SelectedParticipant && body.ChoiceIndex is not null
            || kind == VotingAnswerKind.SelectedChoice && body.ParticipantId is not null)
            return Results.BadRequest(new { error = "contradictory_fields" });

        try
        {
            var view = await grains.GetTenantGrain<IVotingMatchGrain>(id).AnswerAsync(meId, body.Slot,
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

    private static async Task<bool> CategoriesExistAsync(IReadOnlyList<string> ids, IVotingCategoryRepository categories)
    {
        foreach (var id in ids.Distinct())
            if (await categories.GetAsync(id) is null) return false;
        return true;
    }

    private static bool TryParseKind(string? raw, out VotingAnswerKind kind)
    {
        kind = raw?.ToLowerInvariant() switch
        {
            "participant" => VotingAnswerKind.SelectedParticipant,
            "choice" => VotingAnswerKind.SelectedChoice,
            "na" => VotingAnswerKind.NotApplicable,
            "multiple" => VotingAnswerKind.MultipleParticipants,
            "none" => VotingAnswerKind.NoParticipant,
            _ => (VotingAnswerKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    internal static async Task<VotingViewDto> ToDtoAsync(VotingView view, string meId, IPlayerRepository players)
    {
        var people = await players.GetManyAsync(view.Participants.Select(p => p.Id).ToList());
        var lookup = people.ToDictionary(p => p.Id);
        var participants = view.Participants.Select(p =>
        {
            var player = lookup.GetValueOrDefault(p.Id);
            return new VotingParticipantDto(p.Id, player?.DisplayName ?? p.Id, p.Active,
                player?.IsGuest ?? false, player?.AvatarSeed);
        }).ToList();

        VotingSlotDto? Slot(VotingSlotView? slot)
            => slot is null ? null : new VotingSlotDto(slot.Slot, slot.QuestionId, slot.Prompt,
                [.. slot.Options.Select(o => new VotingOptionDto(KindName(o.Kind), o.ParticipantId,
                    o.ParticipantId is null ? null : lookup.GetValueOrDefault(o.ParticipantId)?.DisplayName ?? o.ParticipantId,
                    o.ChoiceIndex, o.Text, o.Kind == (int)VotingAnswerKind.NotApplicable,
                    o.ParticipantId is null ? null : lookup.GetValueOrDefault(o.ParticipantId)?.AvatarSeed))],
                slot.ServedAt, slot.AnsweredParticipantIds,
                [.. slot.Answers.Select(a => new VotingAnswerDto(KindName(a.Kind), a.ParticipantId,
                    a.ChoiceIndex, a.At, a.PlayerId))], ToMediaDto(slot.Media));

        var own = view.OwnAnswer is null ? null : new VotingAnswerDto(KindName(view.OwnAnswer.Kind),
            view.OwnAnswer.ParticipantId, view.OwnAnswer.ChoiceIndex, view.OwnAnswer.At, meId);
        var results = view.Results is null ? null : new VotingResultsDto(
            [.. view.Results.Slots.Select(slot => slot is null ? null
                : new VotingSlotResultDto(slot.Slot, [.. slot.Counts], slot.AllAgreed))],
            view.Results.PairStats is null ? null : [.. view.Results.PairStats.Select(pair =>
                new VotingPairStatDto(pair.FirstParticipantId, pair.SecondParticipantId, pair.Same,
                    pair.Different, pair.AgreementPercent))],
            view.Results.AllAgreedCount);
        return new VotingViewDto(view.Id, view.Code, "voting", ((Language)view.Lang).Code(), view.Capacity,
            ((MatchState)view.State).ToString().ToLowerInvariant(), participants, view.CurrentSlotIndex,
            view.TotalSlots, Slot(view.CurrentSlot), Slot(view.LastClosedSlot), own, view.CreatedAt,
            view.EndedAt, results, view.CategoryIds,
            view.ClosedSlots is null ? null : [.. view.ClosedSlots.Select(Slot).OfType<VotingSlotDto>()]);
    }

    private static string KindName(int kind) => (VotingAnswerKind)kind switch
    {
        VotingAnswerKind.SelectedParticipant => "participant",
        VotingAnswerKind.SelectedChoice => "choice",
        VotingAnswerKind.NotApplicable => "na",
        VotingAnswerKind.MultipleParticipants => "multiple",
        VotingAnswerKind.NoParticipant => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The voting answer kind is not declared.")
    };

    private static MediaDto? ToMediaDto(VotingMediaView? media)
        => media is null || (MediaKind)media.Kind == MediaKind.None
            ? null
            : new MediaDto(((MediaKind)media.Kind).ToString().ToLowerInvariant(), media.Url, media.Attribution);
}
