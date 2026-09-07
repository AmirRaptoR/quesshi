using Microsoft.AspNetCore.Mvc;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

public static class GameEndpoints
{
    /// <summary>
    /// Marks the handful of endpoints a guest is allowed to reach. Its absence denies, so anything
    /// added to this group later is closed to guests until someone decides otherwise — the failure
    /// mode of forgetting is a guest seeing too little, never too much.
    /// </summary>
    private sealed class AllowGuest;

    public static void MapGame(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.AddEndpointFilter(static async (context, next) =>
        {
            var open = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<AllowGuest>() is not null;
            return context.HttpContext.User.IsGuest() && !open
                ? Results.Forbid()
                : await next(context);
        });

        // --- profile -----------------------------------------------------------------
        api.MapGet("/me", async (HttpContext ctx, IPlayerRepository players, ILeaderboard board, IPresence presence) =>
        {
            var me = await players.GetAsync(ctx.User.PlayerId()!);
            return me is null ? Results.Unauthorized() : Results.Ok(me.ToMeDto(await FriendsOfAsync(me, players, board, presence)));
        }).WithMetadata(new AllowGuest());

        api.MapPut("/me", async (UpdateProfileDto body, HttpContext ctx, IPlayerRepository players, ILeaderboard board, IPresence presence) =>
        {
            var me = await players.GetAsync(ctx.User.PlayerId()!);
            if (me is null) return Results.Unauthorized();

            var name = body.DisplayName?.Trim() ?? "";
            if (name.Length is < 2 or > 24) return Results.BadRequest(new { error = "name_length" });

            me.Rename(name);
            me.SetLanguage(body.Lang.ToLanguage());
            await players.UpsertAsync(me);

            return Results.Ok(me.ToMeDto(await FriendsOfAsync(me, players, board, presence)));
        });

        api.MapGet("/categories", async (ICategoryRepository categories, HttpContext ctx,
            IPlayerRepository players, IQuestionRepository questions) =>
        {
            var me = await players.GetAsync(ctx.User.PlayerId()!);
            var lang = me?.Lang ?? Language.Fa;

            var playable = Mappers.PlayableLanguages(await questions.BucketCountsAsync());

            return (await categories.AllAsync())
                .Where(c => c.IsActive)
                .Select(c => c.ToDto(lang, playable.GetValueOrDefault(c.Id, [])))
                .ToList();
        });

        // --- friends -----------------------------------------------------------------
        api.MapPost("/friends/{id}", async (string id, HttpContext ctx, IGrainFactory grains, IPlayerRepository players) =>
        {
            var meId = ctx.User.PlayerId()!;
            if (id == meId) return Results.BadRequest(new { error = "self" });
            if (await players.GetAsync(id) is null) return Results.NotFound();

            // Friendship is mutual: a one-way list makes "challenge a friend" confusing on the other side.
            await grains.GetGrain<IPlayerGrain>(meId).AddFriendAsync(id);
            await grains.GetGrain<IPlayerGrain>(id).AddFriendAsync(meId);
            return Results.Ok();
        });

        api.MapDelete("/friends/{id}", async (string id, HttpContext ctx, IGrainFactory grains) =>
        {
            var meId = ctx.User.PlayerId()!;
            await grains.GetGrain<IPlayerGrain>(meId).RemoveFriendAsync(id);
            await grains.GetGrain<IPlayerGrain>(id).RemoveFriendAsync(meId);
            return Results.Ok();
        });

        api.MapGet("/players/search", async (string? q, IPlayerRepository players) =>
            (await players.SearchAsync(q, 0, 20)).Select(p => new FriendDto(p.Id, p.DisplayName, p.AvatarSeed, p.Stats.TotalScore)).ToList());

        // --- leaderboards ------------------------------------------------------------
        api.MapGet("/leaderboard", async (ILeaderboard board, IPlayerRepository players) =>
            await RowsAsync(await board.TopAsync(20), players));

        api.MapGet("/leaderboard/friends", async (HttpContext ctx, ILeaderboard board, IPlayerRepository players) =>
        {
            var me = await players.GetAsync(ctx.User.PlayerId()!);
            if (me is null) return Results.Unauthorized();

            var ids = me.Friends.Append(me.Id).ToList();
            return Results.Ok(await RowsAsync(await board.AmongAsync(ids), players));
        });

        // --- matches -----------------------------------------------------------------
        api.MapPost("/matches", async (CreateMatchDto body, HttpContext ctx, IGrainFactory grains,
            QuestionSetBuilder builder, IIdFactory ids, IPlayerRepository players) =>
        {
            var meId = ctx.User.PlayerId()!;
            var me = await players.GetAsync(meId);
            if (me is null) return Results.Unauthorized();

            var lang = string.IsNullOrWhiteSpace(body.Lang) ? me.Lang : body.Lang.ToLanguage();

            if (body.Random)
            {
                var queue = grains.GetGrain<IMatchmakingGrain>(0);
                var waitingMatchId = await queue.FindOrQueueAsync(meId, (int)lang, "");

                if (waitingMatchId is { Length: > 0 })
                {
                    var opponentMatch = grains.GetGrain<IMatchGrain>(waitingMatchId);
                    if (await opponentMatch.JoinAsync(meId))
                        return Results.Ok(await SummaryAsync(opponentMatch, meId, players));
                }
            }

            List<Question> set;
            try
            {
                // Anything outside 1..5 is dropped rather than rejected: a nonsense level is the
                // same request as no level at all.
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

            var matchId = ids.NewId();
            var grain = grains.GetGrain<IMatchGrain>(matchId);
            await grain.CreateAsync((int)lang, meId, [.. set.Select(q => q.Id)], ids.NewMatchCode());

            if (body.Random)
                await grains.GetGrain<IMatchmakingGrain>(0).FindOrQueueAsync(meId, (int)lang, matchId);

            return Results.Ok(await SummaryAsync(grain, meId, players));
        });

        api.MapPost("/matches/join/{code}", async (string code, HttpContext ctx, IGrainFactory grains,
            IMatchArchive archive, IPlayerRepository players) =>
            await JoinMatchAsync(code, ctx.User.PlayerId()!, grains, archive, players));

        // Reporting is the whole moderation model now, so it has to be hard to abuse: you may only
        // report a question you were actually served, and only once.
        api.MapPost("/report", async (ReportQuestionDto body, HttpContext ctx,
            IQuestionRepository questions, IMatchArchive archive, IClock clock) =>
            await ReportAsync(body, ctx.User.PlayerId()!, questions, archive, clock));

        api.MapGet("/matches", async (HttpContext ctx, IMatchArchive archive, IPlayerRepository players,
            IGrainFactory grains, bool? active, int? take) =>
            await ListMatchesAsync(ctx.User.PlayerId()!, active ?? false, take, archive, players, grains));

        api.MapGet("/matches/{id}", async (string id, HttpContext ctx, IGrainFactory grains,
            IQuestionRepository questions, ICategoryRepository categories, IPlayerRepository players) =>
        {
            var meId = ctx.User.PlayerId()!;
            var view = await grains.GetGrain<IMatchGrain>(id).GetAsync(meId);
            if (view is null || !IsIn(view, meId)) return Results.NotFound();

            var summary = await ToSummaryAsync(view, meId, players);
            var reveal = summary.CanReveal
                ? await BuildRevealAsync(view, meId, questions, categories)
                : [];

            return Results.Ok(new MatchDetailDto(summary, reveal));
        }).WithMetadata(new AllowGuest());

        api.MapPost("/matches/{id}/next", async (string id, HttpContext ctx, IGrainFactory grains,
            IQuestionRepository questions, ICategoryRepository categories) =>
        {
            var meId = ctx.User.PlayerId()!;
            var served = await grains.GetGrain<IMatchGrain>(id).ServeNextAsync(meId);
            if (served is null) return Results.NoContent();

            var question = await questions.GetAsync(served.QuestionId);
            if (question is null) return Results.Problem("That question has vanished.", statusCode: 500);

            var category = await categories.GetAsync(question.CategoryId);

            // Note what is absent: the correct index never leaves the server before the answer arrives.
            return Results.Ok(new QuestionCardDto(served.Slot, question.Id, question.Prompt, [.. question.Choices],
                question.CategoryId, category?.NameFor(question.Lang) ?? question.CategoryId,
                category?.Icon ?? "◆", category?.Color ?? "#2EC4B6", (int)question.Level,
                ToMediaDto(question.Media),
                served.SecondsLimit, served.Total));
        }).WithMetadata(new AllowGuest());

        api.MapPost("/matches/{id}/answer", async (string id, AnswerDto body, HttpContext ctx, IGrainFactory grains) =>
        {
            var meId = ctx.User.PlayerId()!;
            // -1 is the timeout: the player ran out of clock, and the run still has to move on.
            if (body.ChoiceIndex is < -1 or >= MatchRules.ChoicesPerQuestion)
                return Results.BadRequest(new { error = "bad_choice" });

            try
            {
                var outcome = await grains.GetGrain<IMatchGrain>(id).AnswerAsync(meId, body.Slot, body.ChoiceIndex);
                return Results.Ok(new AnswerResultDto(outcome.Correct, outcome.CorrectIndex, outcome.Score,
                    outcome.Explanation, outcome.RunFinished, outcome.RunScore));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithMetadata(new AllowGuest());
    }

    /// <summary>
    /// Extracted out of the endpoint delegate so a live code's refusal can be driven directly in a
    /// test, the same way <see cref="ListMatchesAsync"/> is.
    /// </summary>
    internal static async Task<IResult> JoinMatchAsync(string code, string meId, IGrainFactory grains,
        IMatchArchive archive, IPlayerRepository players)
    {
        var found = await archive.ByCodeAsync(code);
        if (found is null) return Results.NotFound(new { error = "no_such_code" });
        if (found.IsLive) return Results.BadRequest(new { error = "not_an_async_code" });

        var grain = grains.GetGrain<IMatchGrain>(found.Id);
        if (!await grain.JoinAsync(meId)) return Results.BadRequest(new { error = "cannot_join" });

        return Results.Ok(await SummaryAsync(grain, meId, players));
    }

    /// <summary>
    /// Extracted out of the endpoint delegate so a report's guard can be driven directly in a test,
    /// the same way <see cref="JoinMatchAsync"/> and <see cref="ListMatchesAsync"/> are. A live duel's
    /// archive row carries its <c>QuestionIds</c> just as an async one does, so a question served in
    /// either kind of duel is reportable — the archive row is the ownership check either way.
    /// </summary>
    internal static async Task<IResult> ReportAsync(ReportQuestionDto body, string meId,
        IQuestionRepository questions, IMatchArchive archive, IClock clock)
    {
        if (!Enum.TryParse<ReportReason>(body.Reason, true, out var reason))
            return Results.BadRequest(new { error = "bad_reason" });

        var mine = await archive.ForPlayerAsync(meId, 60);
        if (!mine.Any(m => m.QuestionIds.Contains(body.QuestionId)))
            return Results.BadRequest(new { error = "not_your_question" });

        if (await questions.GetAsync(body.QuestionId) is not { } question) return Results.NotFound();

        var accepted = question.Report(meId, reason, clock.Now);
        if (accepted) await questions.UpsertAsync(question);

        // Already reported by this player is not an error worth surfacing; the button is done either way.
        return Results.Ok(new { reported = true, alreadyReported = !accepted });
    }

    /// <summary>How many archived matches the list will ever look at.</summary>
    private const int MatchListLimit = 40;

    internal static async Task<List<MatchSummaryDto>> ListMatchesAsync(string meId, bool activeOnly, int? take,
        IMatchArchive archive, IPlayerRepository players, IGrainFactory grains)
    {
        var rows = await archive.ForPlayerAsync(meId, MatchListLimit);
        if (activeOnly)
            rows = [.. rows.Where(r => r.State is MatchState.AwaitingOpponent or MatchState.InProgress)];

        // A live duel has no equivalent to IMatchGrain to ask — it is mirrored into its archive row on
        // start and on end (LiveMatchSettlement), and that row already carries everything the list
        // needs. Only an async duel is worth activating a grain for. A no-contest live duel changed
        // nothing and has no result to show, so it is left out entirely — it stays in the archive and
        // is still findable by code, just not in this list.
        var liveRows = rows.Where(r => r.IsLive && r.State != MatchState.NoContest).ToList();
        var asyncRows = rows.Where(r => !r.IsLive).ToList();

        // Asked all at once, so the wait is the slowest single activation rather than the sum of
        // forty. Redaction still happens inside each grain, per player, exactly as it did before.
        var views = await Task.WhenAll(asyncRows.Select(r => grains.GetGrain<IMatchGrain>(r.Id).GetAsync(meId)));
        var asyncViews = views.OfType<MatchView>().ToList();

        // The row was only a way of finding the duel. A grain is written before it is indexed, so a
        // duel that has just been resolved can still be filed as in progress; where the two
        // disagree the grain is the one to believe, and the archive filter above merely saves
        // activating grains that were already finished long ago.
        if (activeOnly)
            asyncViews = [.. asyncViews.Where(v => (MatchState)v.State is MatchState.AwaitingOpponent or MatchState.InProgress)];

        // Who to name is read from the views, not from the archive rows that found them, for async
        // duels: a grain persists itself before it is mirrored into Mongo, so a duel joined a moment
        // ago has an opponent the row does not know about yet. A live duel has no such lag — its row
        // is the only source there is — so its ids come from the row instead. One query either way.
        var names = (await players.GetManyAsync([.. asyncViews
                .SelectMany(v => new[] { v.ChallengerId, v.OpponentId })
                .Concat(liveRows.SelectMany(r => new[] { r.ChallengerId, r.OpponentId }))
                .OfType<string>().Distinct()]))
            .ToDictionary(p => p.Id, p => (p.DisplayName, p.AvatarSeed));

        (string, string) Lookup(string id) => names.TryGetValue(id, out var found) ? found : ("—", id);

        var summaries = asyncViews.Select(v => v.ToSummary(meId, Lookup))
            .Concat(liveRows.Select(r => r.ToLiveSummary(meId, Lookup)));

        // A caller that says how many it will show gets that many. Playable first and newest after,
        // which is the order both pages already put them in, so cutting the list here cannot hide a
        // duel that is waiting on this player behind one that is not. A live duel is never CanPlay,
        // so it can only ever displace another duel that was already waiting, not a playable one.
        return take is { } n
            ? [.. summaries.OrderByDescending(s => s.CanPlay).ThenByDescending(s => s.CreatedAt).Take(n)]
            : [.. summaries];
    }

    private static bool IsIn(MatchView v, string playerId) => v.ChallengerId == playerId || v.OpponentId == playerId;

    private static async Task<MatchSummaryDto?> SummaryAsync(IMatchGrain grain, string meId, IPlayerRepository players)
    {
        var view = await grain.GetAsync(meId);
        return view is null ? null : await ToSummaryAsync(view, meId, players);
    }

    private static async Task<MatchSummaryDto> ToSummaryAsync(MatchView view, string meId, IPlayerRepository players)
    {
        var names = new Dictionary<string, (string, string)>();
        foreach (var id in new[] { view.ChallengerId, view.OpponentId }.OfType<string>().Distinct())
        {
            var p = await players.GetAsync(id);
            names[id] = (p?.DisplayName ?? "—", p?.AvatarSeed ?? id);
        }

        return view.ToSummary(meId, id => names.GetValueOrDefault(id, ("—", id)));
    }

    private static async Task<List<RevealedQuestionDto>> BuildRevealAsync(MatchView view, string meId,
        IQuestionRepository questions, ICategoryRepository categories)
    {
        var all = await questions.GetManyAsync(view.QuestionIds);
        var cats = (await categories.AllAsync()).ToDictionary(c => c.Id);

        var mine = view.Runs.FirstOrDefault(r => r.PlayerId == meId)?.Choices ?? [];
        var otherId = view.ChallengerId == meId ? view.OpponentId : view.ChallengerId;
        var theirs = otherId is null ? [] : view.Runs.FirstOrDefault(r => r.PlayerId == otherId)?.Choices ?? [];

        return [.. all.Select((q, slot) => new RevealedQuestionDto(slot, q.Id, q.Prompt, [.. q.Choices], q.CorrectIndex,
            slot < mine.Count ? mine[slot] : null,
            slot < theirs.Count ? theirs[slot] : null,
            cats.GetValueOrDefault(q.CategoryId)?.NameFor(q.Lang) ?? q.CategoryId,
            q.Explanation,
            ToMediaDto(q.Media)))];
    }

    private static MediaDto? ToMediaDto(MediaRef media)
        => media.Kind == MediaKind.None ? null : new MediaDto(media.Kind.ToString().ToLowerInvariant(), media.Url, media.Attribution);

    /// <summary>Internal so <see cref="Quesshi.Server.Tests"/> can drive it directly, the same way
    /// <see cref="ListMatchesAsync"/> is driven — one bulk presence read for the whole friends list,
    /// never one per friend.</summary>
    internal static async Task<List<FriendDto>> FriendsOfAsync(Player me, IPlayerRepository players, ILeaderboard board, IPresence presence)
    {
        var candidates = new List<Player>();
        foreach (var id in me.Friends)
            if (await players.GetAsync(id) is { } f)
                candidates.Add(f);

        var online = candidates.Count == 0
            ? []
            : await presence.OnlineAsync([.. candidates.Select(f => f.Id)]);

        var friends = candidates.Select(f => new FriendDto(f.Id, f.DisplayName, f.AvatarSeed, f.Stats.TotalScore, online.Contains(f.Id)));
        return [.. friends.OrderByDescending(f => f.Score)];
    }

    private static async Task<List<LeaderboardRowDto>> RowsAsync(IReadOnlyList<LeaderboardEntry> entries, IPlayerRepository players)
    {
        var rows = new List<LeaderboardRowDto>();
        foreach (var e in entries)
        {
            var p = await players.GetAsync(e.PlayerId);
            if (p is not null) rows.Add(new LeaderboardRowDto(e.Rank, p.Id, p.DisplayName, p.AvatarSeed, e.Score));
        }
        return rows;
    }
}

