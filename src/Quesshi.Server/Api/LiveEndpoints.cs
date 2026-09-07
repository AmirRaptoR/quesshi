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

        api.MapPost("", async (CreateMatchDto body, HttpContext ctx, IGrainFactory grains, QuestionSetBuilder builder,
            IIdFactory ids, IMatchArchive archive, IPlayerRepository players, IClock clock) =>
            await CreateAsync(body, ctx.User.PlayerId()!, grains, builder, ids, archive, players, clock));

        api.MapPost("/join/{code}", async (string code, HttpContext ctx, IGrainFactory grains, IMatchArchive archive, IClock clock) =>
            await JoinAsync(code, ctx.User.PlayerId()!, grains, archive, clock)).WithMetadata(new AllowGuest());

        api.MapGet("/{id}", async (string id, HttpContext ctx, IGrainFactory grains, IClock clock) =>
            await GetAsync(id, ctx.User.PlayerId()!, grains, clock)).WithMetadata(new AllowGuest());

        api.MapDelete("/{id}", async (string id, HttpContext ctx, IGrainFactory grains) =>
            await CancelAsync(id, ctx.User.PlayerId()!, grains));
    }

    /// <summary>
    /// Extracted so a test can drive it directly, the same way <see cref="GameEndpoints.JoinMatchAsync"/> is.
    /// </summary>
    internal static async Task<IResult> CreateAsync(CreateMatchDto body, string meId, IGrainFactory grains,
        QuestionSetBuilder builder, IIdFactory ids, IMatchArchive archive, IPlayerRepository players, IClock clock)
    {
        // The random queue and friend challenges are #15's; this endpoint only ever seats a friend
        // who follows the code.
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
            return Results.Ok(view.ToDto(clock.Now));
        }

        return Results.Problem("Could not allocate a share code.", statusCode: 503);
    }

    internal static async Task<IResult> JoinAsync(string code, string meId, IGrainFactory grains, IMatchArchive archive, IClock clock)
    {
        var found = await archive.ByCodeAsync(code);
        if (found is null) return Results.NotFound(new { error = "no_such_code" });
        if (!found.IsLive) return Results.BadRequest(new { error = "not_a_live_code" });

        var grain = grains.GetGrain<ILiveMatchGrain>(found.Id);
        var result = (LiveJoinResult)await grain.JoinAsync(meId);

        return result switch
        {
            LiveJoinResult.Joined or LiveJoinResult.AlreadyIn => Results.Ok((await grain.GetAsync(meId))!.ToDto(clock.Now)),
            LiveJoinResult.SelfJoin => Results.BadRequest(new { error = "self_join" }),
            LiveJoinResult.Expired => Results.BadRequest(new { error = "lobby_expired" }),
            _ => Results.BadRequest(new { error = "cannot_join" }) // Taken, Unknown
        };
    }

    internal static async Task<IResult> GetAsync(string id, string meId, IGrainFactory grains, IClock clock)
    {
        var view = await grains.GetGrain<ILiveMatchGrain>(id).GetAsync(meId);
        return view is null ? Results.NotFound() : Results.Ok(view.ToDto(clock.Now));
    }

    internal static async Task<IResult> CancelAsync(string id, string meId, IGrainFactory grains)
        => await grains.GetGrain<ILiveMatchGrain>(id).CancelAsync(meId)
            ? Results.Ok()
            : Results.BadRequest(new { error = "cannot_cancel" });
}
