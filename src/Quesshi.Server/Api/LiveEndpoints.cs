using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

public static class LiveEndpoints
{
    /// <summary>
    /// Its own opt-in, with the same deny-by-default shape as <c>GameEndpoints.AllowGuest</c>: a
    /// guest may join a live duel by code or link, and nothing else here, until an endpoint asks
    /// for it explicitly.
    /// </summary>
    private sealed class AllowGuest;

    /// <summary>
    /// Opt-in marker for the kill switch: only an endpoint that would start a new live duel carries
    /// this, so <c>GET</c>/<c>DELETE</c> keep working while <c>Live:Enabled</c> is off and a duel
    /// already in flight can still finish. The presence/random-queue sub-issue's own entry points
    /// only need to add this marker.
    /// </summary>
    internal sealed class RequiresLiveEnabled;

    /// <summary>How many fresh codes a create will try before giving up. Collisions are
    /// astronomically unlikely (31^6 codes shared with every async match ever created) — this is a
    /// bound on a failure mode, not a number expected to matter in practice.</summary>
    private const int MaxCodeAttempts = 5;

    public static void MapLive(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/live").RequireAuthorization();

        api.AddEndpointFilter(static async (context, next) =>
        {
            var open = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<AllowGuest>() is not null;
            return context.HttpContext.User.IsGuest() && !open
                ? Results.Forbid()
                : await next(context);
        });

        api.AddEndpointFilter(static async (context, next) =>
        {
            var gated = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<RequiresLiveEnabled>() is not null;
            if (!gated) return await next(context);

            var grains = context.HttpContext.RequestServices.GetRequiredService<IGrainFactory>();
            var enabled = await grains.GetGrain<ILiveSettingsGrain>(0).IsEnabledAsync();
            return enabled ? await next(context) : Results.Json(new { error = "live_disabled" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        api.MapPost("", async (CreateMatchDto body, HttpContext ctx, IGrainFactory grains, QuestionSetBuilder builder,
            IIdFactory ids, IMatchArchive archive, IPlayerRepository players,
            IQuestionRepository questions, ICategoryRepository categories, IClock clock) =>
            await CreateAsync(body, ctx.User.PlayerId()!, grains, builder, ids, archive, players, questions, categories, clock))
            .WithMetadata(new RequiresLiveEnabled());

        api.MapPost("/join/{code}", async (string code, HttpContext ctx, IGrainFactory grains, IMatchArchive archive,
            IPlayerRepository players, IQuestionRepository questions, ICategoryRepository categories, IClock clock) =>
            await JoinAsync(code, ctx.User.PlayerId()!, grains, archive, players, questions, categories, clock))
            .WithMetadata(new AllowGuest())
            .WithMetadata(new RequiresLiveEnabled());

        api.MapGet("/{id}", async (string id, HttpContext ctx, IGrainFactory grains,
            IPlayerRepository players, IQuestionRepository questions, ICategoryRepository categories, IClock clock) =>
            await GetAsync(id, ctx.User.PlayerId()!, grains, players, questions, categories, clock)).WithMetadata(new AllowGuest());

        api.MapDelete("/{id}", async (string id, HttpContext ctx, IGrainFactory grains) =>
            await CancelAsync(id, ctx.User.PlayerId()!, grains));

        // --- lobby lifecycle (issue #52) ----------------------------------------------------------
        // Join is deliberately not repeated here: /join/{code} above already calls the same
        // capacity-aware ILiveMatchGrain.JoinAsync a 2-to-8-seat lobby needs, so an N-player lobby is
        // joined exactly as a 1v1 always was.
        api.MapPost("/lobby", async (CreateLobbyDto body, HttpContext ctx, IGrainFactory grains, IIdFactory ids,
            IMatchArchive archive, IPlayerRepository players, IQuestionRepository questions, ICategoryRepository categories, IClock clock) =>
            await CreateLobbyAsync(body, ctx.User.PlayerId()!, grains, ids, archive, players, questions, categories, clock))
            .WithMetadata(new RequiresLiveEnabled());

        api.MapPost("/{id}/leave", async (string id, HttpContext ctx, IGrainFactory grains) =>
            await grains.GetGrain<ILiveMatchGrain>(id).LeaveAsync(ctx.User.PlayerId()!)
                ? Results.Ok()
                : Results.BadRequest(new { error = "cannot_leave" }));

        api.MapPost("/{id}/start", async (string id, HttpContext ctx, IGrainFactory grains) =>
            await grains.GetGrain<ILiveMatchGrain>(id).StartAsync(ctx.User.PlayerId()!)
                ? Results.Ok()
                : Results.BadRequest(new { error = "cannot_start" }));

        api.MapPut("/{id}/settings", async (string id, UpdateDuelSettingsDto body, HttpContext ctx, IGrainFactory grains, IPlayerRepository players) =>
            await UpdateSettingsAsync(id, body, ctx.User.PlayerId()!, grains, players));
    }

    /// <summary>
    /// Opens an N-player lobby (2-8 seats) with these settings, drawing no questions yet — <c>Start</c>
    /// draws them from whatever the settings say at that instant. <paramref name="body"/>'s question
    /// count is coerced to the default rather than rejected when it is not one of
    /// <c>MatchRules.QuestionCountChoices</c>, mirroring exactly what <see cref="QuestionSetBuilder.BuildAsync"/>
    /// already does for the plain <see cref="CreateAsync"/> path — <c>DuelSettings.Create</c> validates
    /// strictly and would otherwise throw for a count nobody typed on purpose. Capacity is validated
    /// here, before the domain ever sees it, for the same reason: <c>LiveMatch.Create</c> throws for
    /// anything outside 2-8, and an HTTP caller deserves a 400, not a 500, for a bad request body.
    /// </summary>
    internal static async Task<IResult> CreateLobbyAsync(CreateLobbyDto body, string meId, IGrainFactory grains,
        IIdFactory ids, IMatchArchive archive, IPlayerRepository players, IQuestionRepository questions,
        ICategoryRepository categories, IClock clock)
    {
        if (body.Capacity is < 2 or > 8) return Results.BadRequest(new { error = "bad_capacity" });

        var me = await players.GetAsync(meId);
        if (me is null) return Results.Unauthorized();

        var lang = string.IsNullOrWhiteSpace(body.Lang) ? me.Lang : body.Lang.ToLanguage();
        var count = CoerceQuestionCount(body.Questions);
        var levels = CoerceLevels(body.Levels);

        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var code = ids.NewMatchCode();
            if (await archive.ByCodeAsync(code) is not null) continue;

            var matchId = ids.NewId();
            var grain = grains.GetGrain<ILiveMatchGrain>(matchId);
            var view = await grain.CreateLobbyAsync(code, meId, (int)lang, count, body.Categories ?? [], levels, body.Capacity);
            var lookup = await players.LiveLookupAsync(view);
            return Results.Ok(await view.ToLiveDtoAsync(clock.Now, questions, categories, lookup));
        }

        return Results.Problem("Could not allocate a share code.", statusCode: 503);
    }

    internal static async Task<IResult> UpdateSettingsAsync(string id, UpdateDuelSettingsDto body, string meId,
        IGrainFactory grains, IPlayerRepository players)
    {
        var me = await players.GetAsync(meId);
        if (me is null) return Results.Unauthorized();

        var lang = string.IsNullOrWhiteSpace(body.Lang) ? me.Lang : body.Lang.ToLanguage();
        var count = CoerceQuestionCount(body.Questions);
        var levels = CoerceLevels(body.Levels);

        var ok = await grains.GetGrain<ILiveMatchGrain>(id).UpdateSettingsAsync(meId, (int)lang, count, body.Categories ?? [], levels);
        return ok ? Results.Ok() : Results.BadRequest(new { error = "cannot_update_settings" });
    }

    /// <summary>Mirrors <see cref="QuestionSetBuilder.BuildAsync"/>'s own coercion: a count nobody
    /// picked from the offered choices is not a request worth refusing, just one worth defaulting.</summary>
    private static int CoerceQuestionCount(int? questionCount)
    {
        var count = questionCount ?? MatchRules.QuestionsPerMatch;
        return MatchRules.IsValidCount(count) ? count : MatchRules.QuestionsPerMatch;
    }

    /// <summary>Anything outside 1..5 is dropped rather than rejected — the same coercion every other
    /// creation path in this file applies.</summary>
    private static List<int> CoerceLevels(List<int>? levels) => [.. (levels ?? []).Where(l => l is >= 1 and <= 5)];

    /// <summary>
    /// Extracted so a test can drive it directly, the same way <see cref="GameEndpoints.JoinMatchAsync"/> is.
    /// </summary>
    internal static async Task<IResult> CreateAsync(CreateMatchDto body, string meId, IGrainFactory grains,
        QuestionSetBuilder builder, IIdFactory ids, IMatchArchive archive, IPlayerRepository players,
        IQuestionRepository questions, ICategoryRepository categories, IClock clock)
    {
        // The random queue rides LobbyHub.QueueRandom instead — it needs a heartbeat and a push, which
        // a REST endpoint cannot give it. This endpoint only ever seats a friend who follows the code.
        if (body.Random) return Results.BadRequest(new { error = "random_not_supported" });

        var me = await players.GetAsync(meId);
        if (me is null) return Results.Unauthorized();

        var lang = string.IsNullOrWhiteSpace(body.Lang) ? me.Lang : body.Lang.ToLanguage();

        List<Question> set;
        try
        {
            // Anything outside 1..5 is dropped rather than rejected — the same coercion
            // POST /api/matches applies, so a nonsense level here is the same request as no level.
            var levels = body.Levels?
                .Where(l => l is >= 1 and <= 5)
                .Select(l => (Difficulty)l)
                .ToList();

            set = [.. await builder.BuildAsync(lang, body.Categories, body.Questions, levels)];
        }
        catch (NotEnoughQuestionsException ex)
        {
            return Results.Problem(ex.Message, statusCode: 503);
        }

        // Async and live duels share one code namespace in one Matches collection, guarded by a
        // unique index on Code (MongoContext.EnsureIndexesAsync) — that index is what makes a
        // collision impossible, not this check. This check only means a collision is never even
        // attempted in the overwhelmingly common case; the index is the real guarantee, and a
        // duplicate-key write beneath this loop would still refuse to corrupt someone else's duel.
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var code = ids.NewMatchCode();
            if (await archive.ByCodeAsync(code) is not null) continue;

            var matchId = ids.NewId();
            var grain = grains.GetGrain<ILiveMatchGrain>(matchId);
            var view = await grain.CreateAsync(code, (int)lang, meId, [.. set.Select(q => q.Id)]);
            var lookup = await players.LiveLookupAsync(view);
            return Results.Ok(await view.ToLiveDtoAsync(clock.Now, questions, categories, lookup));
        }

        return Results.Problem("Could not allocate a share code.", statusCode: 503);
    }

    internal static async Task<IResult> JoinAsync(string code, string meId, IGrainFactory grains, IMatchArchive archive,
        IPlayerRepository players, IQuestionRepository questions, ICategoryRepository categories, IClock clock)
    {
        var found = await archive.ByCodeAsync(code);
        if (found is null) return Results.NotFound(new { error = "no_such_code" });
        if (!found.IsLive) return Results.BadRequest(new { error = "not_a_live_code" });

        var grain = grains.GetGrain<ILiveMatchGrain>(found.Id);
        var result = (LiveJoinResult)await grain.JoinAsync(meId);

        if (result is not (LiveJoinResult.Joined or LiveJoinResult.AlreadyIn))
            return result switch
            {
                LiveJoinResult.SelfJoin => Results.BadRequest(new { error = "self_join" }),
                LiveJoinResult.Expired => Results.BadRequest(new { error = "lobby_expired" }),
                _ => Results.BadRequest(new { error = "cannot_join" }) // Taken, Unknown
            };

        var view = (await grain.GetAsync(meId))!;
        var lookup = await players.LiveLookupAsync(view);
        return Results.Ok(await view.ToLiveDtoAsync(clock.Now, questions, categories, lookup));
    }

    internal static async Task<IResult> GetAsync(string id, string meId, IGrainFactory grains,
        IPlayerRepository players, IQuestionRepository questions, ICategoryRepository categories, IClock clock)
    {
        var view = await grains.GetGrain<ILiveMatchGrain>(id).GetAsync(meId);
        if (view is null) return Results.NotFound();

        var lookup = await players.LiveLookupAsync(view);
        return Results.Ok(await view.ToLiveDtoAsync(clock.Now, questions, categories, lookup));
    }

    internal static async Task<IResult> CancelAsync(string id, string meId, IGrainFactory grains)
        => await grains.GetGrain<ILiveMatchGrain>(id).CancelAsync(meId)
            ? Results.Ok()
            : Results.BadRequest(new { error = "cannot_cancel" });
}
