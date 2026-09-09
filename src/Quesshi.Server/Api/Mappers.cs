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
    public static MatchSummaryDto ToSummary(this MatchView v, string me, Func<string, (string Name, string Avatar, bool IsGuest)> lookup)
    {
        var myRun = v.Runs.FirstOrDefault(r => r.PlayerId == me);

        // "The other side" for this DTO's own two-player shape (theirs, singular) — reads
        // v.Participants directly now that issue #53 reshaped MatchView away from the
        // ChallengerId/OpponentId pair this used to fall back to. For a capacity-2 duel this names
        // exactly the one other participant it always did; for a capacity>2 duel it is whichever
        // other seat sorts first — a real seat, never a made-up id, same as before the reshape.
        // Outcome below is unaffected — OutcomeFor already ranks every one of v.Runs, never just this
        // single "theirs" pick.
        var otherId = v.Participants.FirstOrDefault(id => id != me);
        var theirRun = otherId is null ? null : v.Runs.FirstOrDefault(r => r.PlayerId == otherId);

        var (myName, myAvatar, _) = lookup(me);
        var mine = new PlayerSideDto(me, myName, myAvatar, myRun?.Score ?? 0, myRun?.Correct ?? 0, myRun?.Answered ?? 0, myRun?.Finished ?? false);

        PlayerSideDto? theirs = null;
        if (otherId is not null)
        {
            var (name, avatar, _) = lookup(otherId);
            theirs = new PlayerSideDto(otherId, name, avatar, theirRun?.Score ?? 0, theirRun?.Correct ?? 0, theirRun?.Answered ?? 0, theirRun?.Finished ?? false);
        }

        var state = (MatchState)v.State;
        var over = state is MatchState.Resolved or MatchState.Forfeited;
        var canReveal = mine.Finished || over;

        var outcome = !over ? "pending" : OutcomeFor(me, v.Runs);

        // Issue #53's lobby page addition: the whole roster, in join order, plus the settings the
        // owner picked (or, for a legacy pre-drawn record, reconstructed with empty categories/levels
        // — see DuelSettings' own remarks) and Capacity. Every field the two-sided Me/Opponent shape
        // above cannot express for a capacity>2 lobby.
        var participants = v.Participants.Select(id =>
        {
            var (name, avatar, isGuest) = lookup(id);
            return new LiveParticipantDto(id, name, avatar, isGuest);
        }).ToList();
        var settings = new DuelSettingsDto(((Language)v.Lang).Code(), v.QuestionCount, v.CategoryIds ?? [], v.Levels ?? []);

        return new MatchSummaryDto(v.Id, v.Code, ((Language)v.Lang).Code(), state.ToString().ToLowerInvariant(),
            mine, theirs, v.WinnerId, v.IsDraw, v.CreatedAt, !over && !mine.Finished, canReveal, outcome,
            v.QuestionIds.Count, IsLive: false, Participants: participants, Capacity: v.Capacity,
            Settings: settings, SettingsLocked: v.QuestionIds.Count > 0);
    }

    /// <summary>
    /// A per-player outcome ranked from every run's own banked score, never from the match's
    /// <c>WinnerId</c>/<c>IsDraw</c> scalars — those name only the top of the standings, and reading
    /// them for an arbitrary player's own result is exactly the bug that would hand a global "draw" to
    /// everyone once first place is shared, not just to whoever actually shares it. For scores of
    /// 100, 100, 50 this gives the two 100s "win"/"draw" (they share first) and the 50 "loss", never
    /// three draws. Ranking purely by score is correct for <see cref="RunView"/>'s own domain, unlike
    /// <c>LiveMatch.Standings</c>: an async run has no per-round abandonment to rank below everyone
    /// else regardless of score (see <c>Match.Standings</c>'s own remarks), so "highest score(s) win,
    /// ties share first" is the entire rule.
    /// </summary>
    private static string OutcomeFor(string playerId, IReadOnlyList<RunView> runs)
    {
        var mine = runs.FirstOrDefault(r => r.PlayerId == playerId);
        if (mine is null || runs.Count == 0) return "loss";

        var top = runs.Max(r => r.Score);
        if (mine.Score != top) return "loss";

        return runs.Count(r => r.Score == top) > 1 ? "draw" : "win";
    }

    /// <summary>
    /// The list's view of a live duel, built straight off its archive row rather than through a
    /// grain: the row is mirrored on start and on end (<c>LiveMatchSettlement</c>), so it already
    /// holds every field the list needs. A live duel is never <c>CanPlay</c> — it advances on its own
    /// clock whether or not this player is looking — so the row offers Rejoin instead of Play.
    /// </summary>
    public static MatchSummaryDto ToLiveSummary(this ArchivedMatch m, string me, Func<string, (string Name, string Avatar, bool IsGuest)> lookup)
    {
        // Every score, mine and theirs, is read off Results by matching PlayerId — never off the
        // legacy ChallengerId/OpponentId-keyed ChallengerScore/OpponentScore this used to switch on.
        // That pair only ever names the first two seats, so for a capacity>2 duel's third-or-later
        // player the old ternary attributed *whichever of those two slots was not literally
        // ChallengerId* to "mine" the instant this player was seated third or later — a live scoring
        // bug (the wrong player's score, shown as this player's own), not a display nicety. otherId
        // still names only a single "opponent" — this DTO's two-sided shape is issue #53's rework, not
        // this one's — but it is now picked from Results, the one place that lists every real seat, so
        // it can never coincide with `me`.
        var myScore = m.Results.FirstOrDefault(r => r.PlayerId == me)?.Score ?? 0;
        var otherId = m.Results.Select(r => r.PlayerId).FirstOrDefault(id => id != me);
        var otherScore = otherId is null ? 0 : m.Results.FirstOrDefault(r => r.PlayerId == otherId)?.Score ?? 0;

        var over = m.State is MatchState.Resolved or MatchState.Abandoned or MatchState.NoContest;

        var (myName, myAvatar, _) = lookup(me);
        var mine = new PlayerSideDto(me, myName, myAvatar, myScore, 0, 0, over);

        PlayerSideDto? theirs = null;
        if (otherId is not null)
        {
            var (name, avatar, _) = lookup(otherId);
            theirs = new PlayerSideDto(otherId, name, avatar, otherScore, 0, 0, over);
        }

        // Every per-player outcome reads Results — the real per-participant Standing this archive row
        // carries — never the two match-wide scalars: WinnerId/IsDraw name only who occupies (or
        // shares) first place, and reading them for an arbitrary player's own result is exactly the
        // bug that hands a global "draw" to everyone once first place is shared. For scores of 100,
        // 100, 50 this reads two "win"/"draw" results and one "loss" from Results, never three draws.
        // NoContest is its own case, kept exactly as before: it credits nobody, so Results carries
        // only the unranked Loss placeholder (see ParticipantResult's own remarks) rather than a real
        // Standing, and "draw" — nobody won, not "everybody lost" — is the honest reading of that.
        string outcome;
        if (!over) outcome = "pending";
        else if (m.State == MatchState.NoContest) outcome = "draw";
        else outcome = m.Results.FirstOrDefault(r => r.PlayerId == me)?.Outcome switch
        {
            MatchOutcome.Win => "win",
            MatchOutcome.Draw => "draw",
            _ => "loss"
        };

        return new MatchSummaryDto(m.Id, m.Code, m.Lang.Code(), m.State.ToString().ToLowerInvariant(),
            mine, theirs, m.WinnerId, m.IsDraw, m.CreatedAt, CanPlay: false, CanReveal: over, outcome,
            m.QuestionIds.Count, IsLive: true);
    }

    /// <summary>
    /// Every player id a <see cref="LiveView"/> mentions, resolved to (name, avatar) in one query —
    /// the live twin of the <c>Func&lt;string,(string,string)&gt;</c> <see cref="ToSummary"/> takes,
    /// built once per request instead of duplicated at every call site.
    /// </summary>
    public static async Task<Func<string, (string Name, string Avatar, bool IsGuest)>> LiveLookupAsync(this IPlayerRepository players, LiveView v)
    {
        var byId = (await players.GetManyAsync(v.Participants)).ToDictionary(p => p.Id);
        return id => byId.TryGetValue(id, out var p) ? (p.DisplayName, p.AvatarSeed, p.IsGuest) : ("—", id, false);
    }

    /// <summary>
    /// The grain's <see cref="LiveView"/> as the wire shape: state and phase become words,
    /// <c>ServerNow</c> is added beside every deadline so a client can measure clock skew once at
    /// connect and never re-sync, and the #13 contract additions are filled in here rather than in
    /// the grain: every seat's name/avatar/guest flag (<paramref name="lookup"/>, mirroring
    /// <see cref="ToSummary"/>) as <see cref="LiveViewDto.Participants"/>, the lobby's derived expiry,
    /// and — for whichever round is current — the prompt/choices/media a client needs to render it
    /// and, once revealed, its explanation. The correct index is never added here beyond what
    /// <see cref="LiveView.Rounds"/> already redacts: <see cref="LiveRoundCardDto"/> has no such field.
    ///
    /// <see cref="LiveViewDto.Participants"/> is issue #53's widening of what used to be only the
    /// first two entries of <see cref="LiveView.Participants"/>, named as a challenger and an
    /// opponent: every seat is carried over now, in the same join order the domain itself keeps.
    /// </summary>
    public static async Task<LiveViewDto> ToLiveDtoAsync(this LiveView v, DateTimeOffset serverNow,
        IQuestionRepository questions, ICategoryRepository categories, Func<string, (string Name, string Avatar, bool IsGuest)> lookup)
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
                card = BuildLiveCard(v.Id, round.Slot, v.TotalRounds, question, category, (Language)v.Lang,
                    round.StartedAt);

                if (phase == LivePhase.Reveal) explanation = question.Explanation;
            }
        }

        var participants = v.Participants.Select(id =>
        {
            var (name, avatar, isGuest) = lookup(id);
            return new LiveParticipantDto(id, name, avatar, isGuest);
        }).ToList();

        var state = (MatchState)v.State;

        return new LiveViewDto(
            v.Id, participants, state.ToString().ToLowerInvariant(),
            phase.ToString().ToLowerInvariant(), v.PhaseEndsAt, serverNow, v.RoundIndex, v.TotalRounds,
            [.. v.Players.Select(p => new LivePlayerViewDto(p.PlayerId, p.Score, p.Correct, p.MissStreak))],
            [.. v.Rounds.Select(r => new LiveRoundResultViewDto(r.Slot, r.QuestionId, r.StartedAt, r.CorrectIndex,
                [.. r.Answers.Select(a => new LiveRoundAnswerViewDto(a.PlayerId, a.Answered, a.ChoiceIndex, a.Correct, a.Score, a.Response))],
                r.Kind, r.CorrectOrder, r.CorrectTarget))],
            BuildColdStandings(v, state, lookup),
            v.WinnerId, v.IsDraw, v.AbandonedBy, v.CreatedAt, v.EndedAt, v.Code,
            phase == LivePhase.Lobby ? v.CreatedAt + LiveRules.LobbyExpires : null,
            card, explanation, v.Capacity,
            new DuelSettingsDto(((Language)v.Lang).Code(), v.QuestionCount, v.CategoryIds ?? [], v.Levels ?? []),
            SettingsLocked: v.TotalRounds > 0);
    }

    /// <summary>
    /// The standings for whoever loads (or reloads) this duel after it is already over, rather than
    /// watching it end live. A connected client never needs this — the "Ended" push carries
    /// <c>LiveEndedDto.Standings</c> — but a cold load has to get the same answer, and the only way to
    /// be sure of that is to carry the domain's own ranking rather than rebuild one from the fields
    /// around it. An earlier version reconstructed it here and could not rank a second abandoner
    /// correctly, because <see cref="LiveView.AbandonedBy"/> names at most one; <c>LiveMatch</c> has
    /// always known the exact order, so it is simply passed through.
    /// </summary>
    /// <summary>
    /// The live card as a cold load (or a reconnect) gets it — the second of the three card
    /// builders, beside <c>GameEndpoints.BuildCard</c> for async and
    /// <c>LiveMatchGrain.BuildRoundCard</c> for the round-start push. It exists as its own method for
    /// the same reason those two do: the items come from <see cref="Question.ServedChoices"/> and
    /// nowhere else, so the arrangement a reconnecting player is shown is the one the round was
    /// opened with and the one their answer will be graded against. A reconnect that reshuffled would
    /// be indistinguishable, from the player's seat, from the game marking a right answer wrong.
    /// <para>
    /// The redaction is the grain's rule restated, not a second, weaker copy of it: shuffled items
    /// for a sort and never the stored order, a base layer and a target shape for a map and never
    /// the target.
    /// </para>
    /// </summary>
    public static LiveRoundCardDto BuildLiveCard(string matchId, int slot, int totalRounds, Question question,
        Category? category, Language lang, DateTimeOffset startedAt)
        => new(slot, totalRounds, question.Id, question.Prompt, [.. question.ServedChoices(matchId, slot)],
            question.CategoryId, category?.NameFor(lang) ?? question.CategoryId,
            category?.Icon ?? "", category?.Color ?? "", (int)question.Level,
            question.Media.Kind == MediaKind.None ? null : new MediaDto(question.Media.Kind.ToString().ToLowerInvariant(), question.Media.Url, question.Media.Attribution),
            startedAt, startedAt + MatchRules.QuestionTime,
            (int)question.Kind, (int?)question.BaseLayer, (int?)question.Target?.Shape);

    private static List<StandingRowDto> BuildColdStandings(LiveView v, MatchState state, Func<string, (string Name, string Avatar, bool IsGuest)> lookup)
    {
        if (state is not (MatchState.Resolved or MatchState.Abandoned)) return [];

        return [.. v.Standings.Select(s =>
        {
            var (name, avatar, _) = lookup(s.PlayerId);
            return new StandingRowDto(s.PlayerId, name, avatar, s.Score, s.Place, OutcomeWord((MatchOutcome)s.Outcome));
        })];
    }

    /// <summary>The wire spelling of an outcome, shared by every standings projection so a live push
    /// and a cold load can never disagree about what to call the same result.</summary>
    private static string OutcomeWord(MatchOutcome outcome) => outcome switch
    {
        MatchOutcome.Win => "win",
        MatchOutcome.Draw => "draw",
        _ => "loss"
    };

    /// <summary>The four pushes <see cref="ILiveNotifier"/> carries, as the wire shape <c>LiveHub</c> sends them in.</summary>
    public static LiveRoundCardDto ToDto(this LiveRoundCard c) => new(
        c.Slot, c.TotalRounds, c.QuestionId, c.Prompt, [.. c.Choices],
        c.CategoryId, c.CategoryName, c.CategoryIcon, c.CategoryColor, (int)c.Level,
        c.Media.Kind == MediaKind.None ? null : new MediaDto(c.Media.Kind.ToString().ToLowerInvariant(), c.Media.Url, c.Media.Attribution),
        c.StartedAt, c.EndsAt, (int)c.Kind, (int?)c.BaseLayer, (int?)c.TargetShape);

    public static LiveRoundRevealDto ToDto(this LiveRoundReveal r) => new(
        r.Slot, r.CorrectIndex, r.Explanation,
        [.. r.Players.Select(p => new LivePlayerRoundDto(p.PlayerId, p.ChoiceIndex, p.Correct, p.RoundScore, p.TotalScore, p.Response))],
        r.EndsAt, (int)r.Kind, r.CorrectOrder, r.CorrectTarget);

    public static LiveEndedDto ToDto(this LiveEnded e) => new(
        e.State.ToString().ToLowerInvariant(), e.WinnerId, e.IsDraw, e.AbandonedBy,
        [.. e.Scores.Select(s => new LivePlayerScoreDto(s.PlayerId, s.Score, s.Correct))],
        [.. e.Standings.Select(s => new StandingDto(s.PlayerId, s.Score, s.Place, s.Outcome.ToString().ToLowerInvariant()))],
        e.Reason);

    public static LivePlayerEliminatedDto ToDto(this LivePlayerEliminated e) => new(e.PlayerId, e.RoundSlot);

    public static RematchOutcomeDto ToDto(this RematchOutcome o) =>
        new(((RematchStatus)o.Status).ToString().ToLowerInvariant(), o.NewMatchId, o.NewMatchCode);
}
