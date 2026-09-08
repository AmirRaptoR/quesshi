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
    }

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
