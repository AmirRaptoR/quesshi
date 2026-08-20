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
        api.MapGet("/me", async (HttpContext ctx, IPlayerRepository players, ILeaderboard board) =>
        {
            var me = await players.GetAsync(ctx.User.PlayerId()!);
            return me is null ? Results.Unauthorized() : Results.Ok(me.ToMeDto(await FriendsOfAsync(me, players, board)));
        }).WithMetadata(new AllowGuest());

        api.MapPut("/me", async (UpdateProfileDto body, HttpContext ctx, IPlayerRepository players, ILeaderboard board) =>
        {
            var me = await players.GetAsync(ctx.User.PlayerId()!);
            if (me is null) return Results.Unauthorized();

            var name = body.DisplayName?.Trim() ?? "";
            if (name.Length is < 2 or > 24) return Results.BadRequest(new { error = "name_length" });

            me.Rename(name);
            me.SetLanguage(body.Lang.ToLanguage());
            await players.UpsertAsync(me);

            return Results.Ok(me.ToMeDto(await FriendsOfAsync(me, players, board)));
        });

        api.MapGet("/categories", async (ICategoryRepository categories, HttpContext ctx, IPlayerRepository players) =>
        {
            var me = await players.GetAsync(ctx.User.PlayerId()!);
            var lang = me?.Lang ?? Language.Fa;
            return (await categories.AllAsync()).Where(c => c.IsActive).Select(c => c.ToDto(lang)).ToList();
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
        {
            var meId = ctx.User.PlayerId()!;
            var found = await archive.ByCodeAsync(code);
            if (found is null) return Results.NotFound(new { error = "no_such_code" });

            var grain = grains.GetGrain<IMatchGrain>(found.Id);
            if (!await grain.JoinAsync(meId)) return Results.BadRequest(new { error = "cannot_join" });

            return Results.Ok(await SummaryAsync(grain, meId, players));
        });

        // Reporting is the whole moderation model now, so it has to be hard to abuse: you may only
        // report a question you were actually served, and only once.
        api.MapPost("/report", async (ReportQuestionDto body, HttpContext ctx,
            IQuestionRepository questions, IMatchArchive archive, IClock clock) =>
        {
            var meId = ctx.User.PlayerId()!;

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
        });

        api.MapGet("/matches", async (HttpContext ctx, IMatchArchive archive, IPlayerRepository players, IGrainFactory grains) =>
        {
            var meId = ctx.User.PlayerId()!;
            var rows = await archive.ForPlayerAsync(meId, 40);

            var summaries = new List<MatchSummaryDto>();
            foreach (var row in rows)
                if (await SummaryAsync(grains.GetGrain<IMatchGrain>(row.Id), meId, players) is { } summary)
                    summaries.Add(summary);

            return summaries;
        });

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

    private static async Task<List<FriendDto>> FriendsOfAsync(Player me, IPlayerRepository players, ILeaderboard board)
    {
        var friends = new List<FriendDto>();
        foreach (var id in me.Friends)
            if (await players.GetAsync(id) is { } f)
                friends.Add(new FriendDto(f.Id, f.DisplayName, f.AvatarSeed, f.Stats.TotalScore));

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

