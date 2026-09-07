using System.Security.Claims;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

public static class Mappers
{
    public static string? PlayerId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>Read from the token, not from storage: the gate has to hold before anything is loaded.</summary>
    public static bool IsGuest(this ClaimsPrincipal user) => user.HasClaim(Auth.TokenIssuer.GuestClaim, "1");

    public static string Code(this Language lang) => lang switch
    {
        Language.Fa => "fa",
        Language.Nl => "nl",
        _ => "en"
    };

    public static Language ToLanguage(this string? code)
        => code?.ToLowerInvariant() switch
        {
            "en" => Language.En,
            "nl" => Language.Nl,
            _ => Language.Fa
        };

    public static StatsDto ToDto(this PlayerStats s) => new(s.Wins, s.Losses, s.Draws, s.Streak, s.BestStreak, s.TotalScore, s.Played);

    public static MeDto ToMeDto(this Player p, List<FriendDto> friends) => new(
        p.Id, p.DisplayName, p.IsGuest ? "" : p.Email, p.AvatarSeed, p.Lang.Code(), p.Stats.ToDto(),
        p.ByCategory.ToDictionary(kv => kv.Key, kv => p.Accuracy(kv.Key)), friends, p.IsGuest);

    public static CategoryDto ToDto(this Category c, Language lang)
        => new(c.Id, c.NameFor(lang), c.NameFa, c.NameEn, c.Icon, c.Color, c.IsActive, c.SortOrder, c.NameNl);

    /// <summary>
    /// Which languages each category can actually be played in, keyed by category id. One approved
    /// question is enough: the client only needs to know whether to offer it, not how deep it is.
    /// </summary>
    public static Dictionary<string, List<string>> PlayableLanguages(IEnumerable<BucketCount> buckets)
        => buckets
            .Where(b => b.Approved > 0)
            .GroupBy(b => b.CategoryId)
            .ToDictionary(g => g.Key, g => g.Select(b => b.Lang.Code()).Distinct().Order().ToList());

    /// <summary>The same, plus which languages the category holds approved questions in.</summary>
    public static CategoryDto ToDto(this Category c, Language lang, IReadOnlyCollection<string> langs)
        => new(c.Id, c.NameFor(lang), c.NameFa, c.NameEn, c.Icon, c.Color, c.IsActive, c.SortOrder, c.NameNl, [.. langs]);

    public static AdminQuestionDto ToAdminDto(this Question q) => new(
        q.Id, q.Lang.Code(), q.CategoryId, (int)q.Level, q.Prompt, [.. q.Choices], q.CorrectIndex, q.Explanation,
        q.Status.ToString().ToLowerInvariant(), q.Source.ToString().ToLowerInvariant(),
        q.Media.Kind == MediaKind.None ? null : new MediaDto(q.Media.Kind.ToString().ToLowerInvariant(), q.Media.Url, q.Media.Attribution),
        q.CreatedAt, q.TimesServed, q.TimesCorrect,
        q.ReportCount,
        [.. q.Reports.Select(r => new QuestionReportDto(r.PlayerId, "", r.Reason.ToString().ToLowerInvariant(), r.At))]);

    public static AdminUserDto ToAdminDto(this Player p)
        => new(p.Id, p.DisplayName, p.Email, p.AvatarSeed, p.IsBanned, p.Stats.ToDto(), p.CreatedAt);

    public static GenerationRunDto ToDto(this GenerationRun r)
        => new(r.Id, r.StartedAt, r.FinishedAt, r.Requested, r.Inserted, r.Rejected, r.Error);

    public static AiSpendDto ToDto(this AiSpend s)
        => new(s.Calls, s.PromptTokens, s.CompletionTokens, s.Cost);

    /// <summary>
    /// Turns the grain's view into what this player is allowed to see. The grain has already
    /// redacted the opponent's answers; this only decides wording and what the UI may offer.
    /// </summary>
    public static MatchSummaryDto ToSummary(this MatchView v, string me, Func<string, (string Name, string Avatar)> lookup)
    {
        var myRun = v.Runs.FirstOrDefault(r => r.PlayerId == me);
        var otherId = v.ChallengerId == me ? v.OpponentId : v.ChallengerId;
        var theirRun = otherId is null ? null : v.Runs.FirstOrDefault(r => r.PlayerId == otherId);

        var (myName, myAvatar) = lookup(me);
        var mine = new PlayerSideDto(me, myName, myAvatar, myRun?.Score ?? 0, myRun?.Correct ?? 0, myRun?.Answered ?? 0, myRun?.Finished ?? false);

        PlayerSideDto? theirs = null;
        if (otherId is not null)
        {
            var (name, avatar) = lookup(otherId);
            theirs = new PlayerSideDto(otherId, name, avatar, theirRun?.Score ?? 0, theirRun?.Correct ?? 0, theirRun?.Answered ?? 0, theirRun?.Finished ?? false);
        }

        var state = (MatchState)v.State;
        var over = state is MatchState.Resolved or MatchState.Forfeited;
        var canReveal = mine.Finished || over;

        var outcome = !over ? "pending"
            : v.IsDraw ? "draw"
            : v.WinnerId == me ? "win"
            : v.WinnerId is null ? "draw" : "loss";

        return new MatchSummaryDto(v.Id, v.Code, ((Language)v.Lang).Code(), state.ToString().ToLowerInvariant(),
            mine, theirs, v.WinnerId, v.IsDraw, v.CreatedAt, !over && !mine.Finished, canReveal, outcome,
            v.QuestionIds.Count);
    }

    /// <summary>
    /// The list's view of a live duel, built straight off its archive row rather than through a
    /// grain: the row is mirrored on start and on end (<c>LiveMatchSettlement</c>), so it already
    /// holds every field the list needs. A live duel is never <c>CanPlay</c> — it advances on its own
    /// clock whether or not this player is looking — so the row offers Rejoin instead of Play.
    /// </summary>
    public static MatchSummaryDto ToLiveSummary(this ArchivedMatch m, string me, Func<string, (string Name, string Avatar)> lookup)
    {
        var otherId = m.ChallengerId == me ? m.OpponentId : m.ChallengerId;
        var (myScore, otherScore) = m.ChallengerId == me
            ? (m.ChallengerScore, m.OpponentScore)
            : (m.OpponentScore, m.ChallengerScore);

        var over = m.State is MatchState.Resolved or MatchState.Abandoned or MatchState.NoContest;

        var (myName, myAvatar) = lookup(me);
        var mine = new PlayerSideDto(me, myName, myAvatar, myScore, 0, 0, over);

        PlayerSideDto? theirs = null;
        if (otherId is not null)
        {
            var (name, avatar) = lookup(otherId);
            theirs = new PlayerSideDto(otherId, name, avatar, otherScore, 0, 0, over);
        }

        var outcome = !over ? "pending"
            : m.IsDraw ? "draw"
            : m.WinnerId == me ? "win"
            : m.WinnerId is null ? "draw" : "loss";

        return new MatchSummaryDto(m.Id, m.Code, m.Lang.Code(), m.State.ToString().ToLowerInvariant(),
            mine, theirs, m.WinnerId, m.IsDraw, m.CreatedAt, CanPlay: false, CanReveal: over, outcome,
            m.QuestionIds.Count, IsLive: true);
    }

    /// <summary>
    /// Every player id a <see cref="LiveView"/> mentions, resolved to (name, avatar) in one query —
    /// the live twin of the <c>Func&lt;string,(string,string)&gt;</c> <see cref="ToSummary"/> takes,
    /// built once per request instead of duplicated at every call site.
    /// </summary>
    public static async Task<Func<string, (string Name, string Avatar)>> LiveLookupAsync(this IPlayerRepository players, LiveView v)
    {
        List<string> ids = v.OpponentId is null ? [v.ChallengerId] : [v.ChallengerId, v.OpponentId];
        var byId = (await players.GetManyAsync(ids)).ToDictionary(p => p.Id);
        return id => byId.TryGetValue(id, out var p) ? (p.DisplayName, p.AvatarSeed) : ("—", id);
    }

    /// <summary>
    /// The grain's <see cref="LiveView"/> as the wire shape: state and phase become words,
    /// <c>ServerNow</c> is added beside every deadline so a client can measure clock skew once at
    /// connect and never re-sync, and the #13 contract additions are filled in here rather than in
    /// the grain: both players' name/avatar (<paramref name="lookup"/>, mirroring
    /// <see cref="ToSummary"/>), the lobby's derived expiry, and — for whichever round is current —
    /// the prompt/choices/media a client needs to render it and, once revealed, its explanation.
    /// The correct index is never added here beyond what <see cref="LiveView.Rounds"/> already
    /// redacts: <see cref="LiveRoundCardDto"/> has no such field.
    /// </summary>
    public static async Task<LiveViewDto> ToLiveDtoAsync(this LiveView v, DateTimeOffset serverNow,
        IQuestionRepository questions, ICategoryRepository categories, Func<string, (string Name, string Avatar)> lookup)
    {
        var phase = (LivePhase)v.Phase;
        LiveRoundCardDto? card = null;
        string? explanation = null;

        if (phase is LivePhase.Question or LivePhase.Reveal && v.RoundIndex < v.Rounds.Count)
        {
            var round = v.Rounds[v.RoundIndex];
            var question = await questions.GetAsync(round.QuestionId);
            if (question is not null)
            {
                var category = await categories.GetAsync(question.CategoryId);
                card = new LiveRoundCardDto(round.Slot, v.TotalRounds, question.Id, question.Prompt, [.. question.Choices],
                    question.CategoryId, category?.NameFor((Language)v.Lang) ?? question.CategoryId,
                    category?.Icon ?? "", category?.Color ?? "", (int)question.Level,
                    question.Media.Kind == MediaKind.None ? null : new MediaDto(question.Media.Kind.ToString().ToLowerInvariant(), question.Media.Url, question.Media.Attribution),
                    round.StartedAt, round.StartedAt + MatchRules.QuestionTime);

                if (phase == LivePhase.Reveal) explanation = question.Explanation;
            }
        }

        var (challengerName, challengerAvatar) = lookup(v.ChallengerId);
        var (opponentName, opponentAvatar) = v.OpponentId is null ? (null, null) : ((string?, string?))lookup(v.OpponentId);

        return new LiveViewDto(
            v.Id, v.ChallengerId, v.OpponentId, ((MatchState)v.State).ToString().ToLowerInvariant(),
            phase.ToString().ToLowerInvariant(), v.PhaseEndsAt, serverNow, v.RoundIndex, v.TotalRounds,
            [.. v.Players.Select(p => new LivePlayerViewDto(p.PlayerId, p.Score, p.Correct, p.MissStreak))],
            [.. v.Rounds.Select(r => new LiveRoundResultViewDto(r.Slot, r.QuestionId, r.StartedAt, r.CorrectIndex,
                [.. r.Answers.Select(a => new LiveRoundAnswerViewDto(a.PlayerId, a.Answered, a.ChoiceIndex, a.Correct, a.Score))]))],
            v.WinnerId, v.IsDraw, v.AbandonedBy, v.CreatedAt, v.EndedAt, v.Code,
            challengerName, challengerAvatar, opponentName, opponentAvatar,
            phase == LivePhase.Lobby ? v.CreatedAt + LiveRules.LobbyExpires : null,
            card, explanation);
    }

    /// <summary>The four pushes <see cref="ILiveNotifier"/> carries, as the wire shape <c>LiveHub</c> sends them in.</summary>
    public static LiveRoundCardDto ToDto(this LiveRoundCard c) => new(
        c.Slot, c.TotalRounds, c.QuestionId, c.Prompt, [.. c.Choices],
        c.CategoryId, c.CategoryName, c.CategoryIcon, c.CategoryColor, (int)c.Level,
        c.Media.Kind == MediaKind.None ? null : new MediaDto(c.Media.Kind.ToString().ToLowerInvariant(), c.Media.Url, c.Media.Attribution),
        c.StartedAt, c.EndsAt);

    public static LiveRoundRevealDto ToDto(this LiveRoundReveal r) => new(
        r.Slot, r.CorrectIndex, r.Explanation,
        [.. r.Players.Select(p => new LivePlayerRoundDto(p.PlayerId, p.ChoiceIndex, p.Correct, p.RoundScore, p.TotalScore))],
        r.EndsAt);

    public static LiveEndedDto ToDto(this LiveEnded e) => new(
        e.State.ToString().ToLowerInvariant(), e.WinnerId, e.IsDraw, e.AbandonedBy,
        [.. e.Scores.Select(s => new LivePlayerScoreDto(s.PlayerId, s.Score, s.Correct))], e.Reason);
}
