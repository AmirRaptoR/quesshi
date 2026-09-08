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

        // Search results are candidates to add, not yet friends — online status has no meaning here.
        api.MapGet("/players/search", async (string? q, IPlayerRepository players) =>
            (await players.SearchAsync(q, 0, 20)).Select(p => new FriendDto(p.Id, p.DisplayName, p.AvatarSeed, p.Stats.TotalScore, false, p.IsGuest)).ToList());

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

        // --- async lobby lifecycle (issue #52) -----------------------------------------------------
        // Join is deliberately not repeated here: /matches/join/{code} above already calls the same
        // capacity-aware IMatchGrain.JoinAsync a 2-to-8-seat lobby needs, so an N-player async lobby is
        // joined exactly as a 1v1 always was.
        api.MapPost("/matches/lobby", async (CreateLobbyDto body, HttpContext ctx, IGrainFactory grains, IIdFactory ids, IPlayerRepository players) =>
            await CreateLobbyAsync(body, ctx.User.PlayerId()!, grains, ids, players));

        api.MapPost("/matches/{id}/leave", async (string id, HttpContext ctx, IGrainFactory grains) =>
            await grains.GetGrain<IMatchGrain>(id).LeaveAsync(ctx.User.PlayerId()!)
                ? Results.Ok()
                : Results.BadRequest(new { error = "cannot_leave" }));

        api.MapPost("/matches/{id}/start", async (string id, HttpContext ctx, IGrainFactory grains) =>
            await grains.GetGrain<IMatchGrain>(id).StartAsync(ctx.User.PlayerId()!)
                ? Results.Ok()
                : Results.BadRequest(new { error = "cannot_start" }));

        api.MapPut("/matches/{id}/settings", async (string id, UpdateDuelSettingsDto body, HttpContext ctx, IGrainFactory grains, IPlayerRepository players) =>
            await UpdateSettingsAsync(id, body, ctx.User.PlayerId()!, grains, players));

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
            var standings = await BuildStandingsAsync(view, players);

            return Results.Ok(new MatchDetailDto(summary, reveal, standings));
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
    /// Opens an N-player async lobby (2-8 seats) with these settings, drawing no questions yet —
    /// <c>Start</c> draws them from whatever the settings say at that instant. Mirrors
    /// <see cref="LiveEndpoints.CreateLobbyAsync"/> exactly, capacity validation and question-count
    /// coercion included, other than not retrying a colliding code — the plain <c>POST /matches</c>
    /// above does not either, and a lobby-create should not behave differently from the creation path
    /// it sits beside.
    /// </summary>
    internal static async Task<IResult> CreateLobbyAsync(CreateLobbyDto body, string meId, IGrainFactory grains,
        IIdFactory ids, IPlayerRepository players)
    {
        if (body.Capacity is < 2 or > 8) return Results.BadRequest(new { error = "bad_capacity" });

        var me = await players.GetAsync(meId);
        if (me is null) return Results.Unauthorized();

        var lang = string.IsNullOrWhiteSpace(body.Lang) ? me.Lang : body.Lang.ToLanguage();
        var count = CoerceQuestionCount(body.Questions);
        var levels = CoerceLevels(body.Levels);

        var matchId = ids.NewId();
        var grain = grains.GetGrain<IMatchGrain>(matchId);
        var view = await grain.CreateLobbyAsync(ids.NewMatchCode(), meId, (int)lang, count, body.Categories ?? [], levels, body.Capacity);

        return Results.Ok(await ToSummaryAsync(view, meId, players));
    }

    internal static async Task<IResult> UpdateSettingsAsync(string id, UpdateDuelSettingsDto body, string meId,
        IGrainFactory grains, IPlayerRepository players)
    {
        var me = await players.GetAsync(meId);
        if (me is null) return Results.Unauthorized();

        var lang = string.IsNullOrWhiteSpace(body.Lang) ? me.Lang : body.Lang.ToLanguage();
        var count = CoerceQuestionCount(body.Questions);
        var levels = CoerceLevels(body.Levels);

        var ok = await grains.GetGrain<IMatchGrain>(id).UpdateSettingsAsync(meId, (int)lang, count, body.Categories ?? [], levels);
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
                .SelectMany(ParticipantIds)
                .Concat(liveRows.SelectMany(r => r.Results.Select(rr => rr.PlayerId)))
                .Distinct()]))
            .ToDictionary(p => p.Id, p => (p.DisplayName, p.AvatarSeed, p.IsGuest));

        (string, string, bool) Lookup(string id) => names.TryGetValue(id, out var found) ? found : ("—", id, false);

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

    /// <summary>
    /// Every seat <paramref name="v"/> actually holds, for a capacity-anything async duel:
    /// <see cref="MatchView.Participants"/> directly, now that issue #53 reshaped it away from the two
    /// legacy scalars this used to reconstruct from (plus whoever had a <see cref="RunView"/>, since a
    /// third-or-later seat that had only just been served a question was otherwise invisible). A seat
    /// is real from the moment <c>Join</c> fills it, whether or not that player has served a question
    /// yet, and every "the other participant" lookup in this file is built on this instead of the
    /// old scalar pair.
    /// </summary>
    private static IEnumerable<string> ParticipantIds(MatchView v) => v.Participants;

    private static bool IsIn(MatchView v, string playerId) => v.Participants.Contains(playerId);

    private static async Task<MatchSummaryDto?> SummaryAsync(IMatchGrain grain, string meId, IPlayerRepository players)
    {
        var view = await grain.GetAsync(meId);
        return view is null ? null : await ToSummaryAsync(view, meId, players);
    }

    private static async Task<MatchSummaryDto> ToSummaryAsync(MatchView view, string meId, IPlayerRepository players)
    {
        var names = new Dictionary<string, (string, string, bool)>();
        foreach (var id in ParticipantIds(view))
        {
            var p = await players.GetAsync(id);
            names[id] = (p?.DisplayName ?? "—", p?.AvatarSeed ?? id, p?.IsGuest ?? false);
        }

        return view.ToSummary(meId, id => names.GetValueOrDefault(id, ("—", id, false)));
    }

    private static async Task<List<RevealedQuestionDto>> BuildRevealAsync(MatchView view, string meId,
        IQuestionRepository questions, ICategoryRepository categories)
    {
        var all = await questions.GetManyAsync(view.QuestionIds);
        var cats = (await categories.AllAsync()).ToDictionary(c => c.Id);

        var mine = view.Runs.FirstOrDefault(r => r.PlayerId == meId)?.Choices ?? [];

        // RevealedQuestionDto is a two-sided (mine/theirs) shape, same reasoning as MatchSummaryDto's
        // own mine/theirs: for a capacity-2 duel "theirs" is unambiguous and this picks exactly the id
        // it always did. For a capacity>2 duel there is no single "other side" any more, so this names
        // whichever other real participant (see ParticipantIds) sorts first — a real answer set, never
        // a made-up id — rather than pretending the duel is still 1v1 or crashing on a missing choice.
        var otherId = ParticipantIds(view).FirstOrDefault(id => id != meId);
        var theirs = otherId is null ? [] : view.Runs.FirstOrDefault(r => r.PlayerId == otherId)?.Choices ?? [];

        return [.. all.Select((q, slot) => new RevealedQuestionDto(slot, q.Id, q.Prompt, [.. q.Choices], q.CorrectIndex,
            slot < mine.Count ? mine[slot] : null,
            slot < theirs.Count ? theirs[slot] : null,
            cats.GetValueOrDefault(q.CategoryId)?.NameFor(q.Lang) ?? q.CategoryId,
            q.Explanation,
            ToMediaDto(q.Media)))];
    }

    /// <summary>
    /// The results screen's standings list: every seat ranked purely by banked score, ties sharing a
    /// place. An async run has no round-by-round abandonment to rank below everyone else regardless
    /// of score the way a live duel's <c>Standing</c> does (see <c>Mappers.OutcomeFor</c>'s own
    /// remarks) — "highest score(s) win, ties share first" is the entire rule, whether every run
    /// finished normally or the match ended by forfeiture with some still open. Empty while the duel
    /// is still running, and for <c>NoContest</c>, which credits nobody, mirroring a live duel's own
    /// <c>Standings</c>.
    /// </summary>
    private static async Task<List<StandingRowDto>> BuildStandingsAsync(MatchView view, IPlayerRepository players)
    {
        var state = (MatchState)view.State;
        if (state is not (MatchState.Resolved or MatchState.Forfeited)) return [];

        var byId = (await players.GetManyAsync(view.Participants)).ToDictionary(p => p.Id);
        (string Name, string Avatar) Lookup(string id) => byId.TryGetValue(id, out var p) ? (p.DisplayName, p.AvatarSeed) : ("—", id);

        var ranked = view.Participants
            .Select(id => (PlayerId: id, Run: view.Runs.FirstOrDefault(r => r.PlayerId == id)))
            .OrderByDescending(p => p.Run?.Score ?? 0)
            .ToList();

        var places = new int[ranked.Count];
        for (var i = 0; i < ranked.Count; i++)
            places[i] = i > 0 && (ranked[i].Run?.Score ?? 0) == (ranked[i - 1].Run?.Score ?? 0) ? places[i - 1] : i + 1;

        // Outcome depends on the final shape of first place, only known once every place is assigned:
        // its sole occupant wins, several sharing it each draw, everyone else loses — the same rule
        // LiveMatch.BuildStandings applies, so scores of 100/100/50 read as two draws and one loss here too.
        var firstPlaceCount = places.Count(p => p == 1);

        return [.. ranked.Select((p, i) =>
        {
            var (name, avatar) = Lookup(p.PlayerId);
            var outcome = places[i] != 1 ? "loss" : firstPlaceCount == 1 ? "win" : "draw";

            // A run that never finished before the match itself ended (forfeiture) is expired, not
            // "still playing" — Resolved never reaches here with an unfinished run, since resolution
            // itself requires every participant to have finished.
            return new StandingRowDto(p.PlayerId, name, avatar, p.Run?.Score ?? 0, places[i], outcome, Expired: p.Run?.Finished != true);
        })];
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

        var friends = candidates.Select(f => new FriendDto(f.Id, f.DisplayName, f.AvatarSeed, f.Stats.TotalScore, online.Contains(f.Id), f.IsGuest));
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

