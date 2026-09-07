using Microsoft.Extensions.Logging;
using System.Text.Json;
using Orleans;
using Orleans.Runtime;
using Quesshi.Grains.Abstractions;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Grains;

/// <summary>
/// Runs one live duel. Unlike <see cref="MatchGrain"/>, time passes here whether or not a player is
/// looking: a grain timer armed for <see cref="LiveMatch.NextDueAt"/> drives every phase change, a
/// coarse reminder is the restart safety net once the timer dies with the activation, and
/// <see cref="ILiveNotifier"/> is the one door state leaves through — the grain never knows whether
/// SignalR, a test fake, or nothing at all is listening on the other side.
/// </summary>
public sealed class LiveMatchGrain(
    [PersistentState("live", "hot")] IPersistentState<LiveMatchStateRecord> state,
    IQuestionRepository questions,
    ICategoryRepository categories,
    ILiveNotifier notifier,
    IMatchArchive archive,
    ILiveDirectory directory,
    QuestionSetBuilder questionSetBuilder,
    IIdFactory ids,
    IClock clock,
    ILogger<LiveMatchGrain> logger) : Grain, ILiveMatchGrain, IRemindable
{
    private const string SafetyNetReminder = "live-safety-net";

    /// <summary>Mirrors <c>LiveLobbyGrain.MaxCodeAttempts</c>: how many fresh codes a rematch's new duel will try before giving up.</summary>
    private const int MaxCodeAttempts = 5;
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinimumDueTime = TimeSpan.FromMilliseconds(1);

    /// <summary>Headroom on top of the timer's due time, so a slightly-late tick still finds the activation alive.</summary>
    private static readonly TimeSpan DeactivationSlack = TimeSpan.FromSeconds(5);

    private LiveMatch? _match;
    private IGrainTimer? _timer;

    /// <summary>How many rounds have already had <c>RoundStarted</c>/<c>RoundRevealed</c> emitted, in this activation's lifetime.</summary>
    private int _startedThrough;
    private int _revealedThrough;

    public override Task OnActivateAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state.State.Json)) return Task.CompletedTask;

        _match = LiveMatch.FromSnapshot(JsonSerializer.Deserialize<LiveMatchSnapshot>(state.State.Json)!);
        // Whatever this snapshot already contains was already announced before we deactivated —
        // reactivating must not replay it, only pick the clock back up from here.
        _startedThrough = _match.Rounds.Count;
        _revealedThrough = ClosedRoundCount(_match);

        var phaseBefore = _match.Phase;
        var wasOver = _match.IsOver;
        _match.Advance(clock.Now); // fast-forward through anything missed while deactivated
        return AfterChangeAsync(phaseBefore, wasOver);
    }

    public async Task<LiveView> CreateAsync(string code, int lang, string challengerId, List<string> questionIds)
    {
        if (_match is not null) return await ViewAsync(_match, challengerId);

        _match = LiveMatch.Create(this.GetPrimaryKeyString(), code, (Language)lang, challengerId, questionIds, clock.Now);

        // A lobby nobody joins must still expire even with the grain deactivated, so the reminder
        // is registered here rather than waiting for the first phase transition.
        await this.RegisterOrUpdateReminder(SafetyNetReminder, ReminderPeriod, ReminderPeriod);
        await AfterChangeAsync(LivePhase.Lobby, false);
        await IndexAsync(); // mirrored so the code is resolvable at all — a grain nobody has indexed can never be found
        return await ViewAsync(_match, challengerId);
    }

    public async Task<int> JoinAsync(string playerId)
    {
        if (_match is null) return (int)LiveJoinResult.Unknown;

        var phaseBefore = _match.Phase;
        var wasOver = _match.IsOver;

        // TryJoin settles the clock first even on a call that is then refused (the lobby may have
        // just expired) — that still has to be persisted, so every outcome but SelfJoin — which
        // touches nothing — goes through AfterChangeAsync.
        var result = _match.TryJoin(playerId, clock.Now);
        if (result != LiveJoinResult.SelfJoin) await AfterChangeAsync(phaseBefore, wasOver);
        if (result == LiveJoinResult.Joined) await IndexAsync(); // the opponent is now part of the row a code resolves to

        return (int)result;
    }

    public async Task<bool> CancelAsync(string playerId)
    {
        if (_match is null || playerId != _match.ChallengerId || _match.Phase != LivePhase.Lobby) return false;

        var phaseBefore = _match.Phase;
        _match.EndNoContest(clock.Now);
        await AfterChangeAsync(phaseBefore, false, "cancelled by challenger");
        return true;
    }

    public async Task<bool> AnswerAsync(string playerId, int slot, int choiceIndex)
    {
        if (_match is null || _match.IsOver) return false;
        if (slot < 0 || slot >= _match.QuestionIds.Count) return false;

        var question = await questions.GetAsync(_match.QuestionIds[slot]);
        if (question is null) return false;

        var correct = question.IsCorrect(choiceIndex);
        var phaseBefore = _match.Phase;
        var wasOver = _match.IsOver;
        var answeredBefore = _match.CurrentRound?.Answers.Count ?? 0;

        try
        {
            // LiveMatch.Answer calls Advance first, so an answer arriving after the buzzer is
            // treated as late rather than scored into a round that has already closed.
            _match.Answer(playerId, slot, choiceIndex, correct, clock.Now, question.Level);
        }
        catch (InvalidOperationException)
        {
            await AfterChangeAsync(phaseBefore, wasOver);
            return false;
        }

        question.RecordServed(correct);
        await questions.UpsertAsync(question);

        // Only the first answer of a round gets this push — the second one closes the round, and
        // RoundRevealedAsync (fired from AfterChangeAsync below) supersedes it.
        if (answeredBefore == 0 && _match.Phase == LivePhase.Question)
            await SafeNotifyAsync(() => notifier.OpponentAnsweredAsync(_match.Id, slot, playerId));

        await AfterChangeAsync(phaseBefore, wasOver);
        return true;
    }

    public async Task<LiveView?> GetAsync(string forPlayerId)
    {
        if (_match is null || !_match.IsParticipant(forPlayerId)) return null;
        return await ViewAsync(_match, forPlayerId);
    }

    public async Task EndAsync(string reason)
    {
        if (_match is null || _match.IsOver) return;

        var phaseBefore = _match.Phase;
        _match.EndNoContest(clock.Now);
        await AfterChangeAsync(phaseBefore, false, reason);
    }

    public async Task<RematchOutcome> RequestRematchAsync(string playerId)
    {
        if (_match is null || !_match.IsOver || _match.OpponentId is null || !_match.IsParticipant(playerId))
            return new RematchOutcome((int)RematchStatus.Refused);

        var now = clock.Now;
        PruneExpiredReadiness(now);

        state.State.RematchReadyAt[playerId] = now;
        await state.WriteStateAsync();

        var opponentId = playerId == _match.ChallengerId ? _match.OpponentId : _match.ChallengerId;
        if (!state.State.RematchReadyAt.ContainsKey(opponentId))
        {
            await SafeNotifyAsync(() => notifier.RematchRequestedAsync(_match.Id, playerId));
            return new RematchOutcome((int)RematchStatus.Waiting);
        }

        if (!await GrainFactory.GetGrain<ILiveSettingsGrain>(0).IsEnabledAsync())
            return await FailRematchAsync();

        List<Question> set;
        try
        {
            set = [.. await questionSetBuilder.BuildAsync(_match.Lang, null, _match.QuestionIds.Count, null)];
        }
        catch (NotEnoughQuestionsException)
        {
            return await FailRematchAsync();
        }

        var newMatchId = await CreateRematchDuelAsync(set);
        if (newMatchId is null) return await FailRematchAsync();

        state.State.RematchReadyAt.Clear();
        await state.WriteStateAsync();
        await SafeNotifyAsync(() => notifier.RematchCreatedAsync(_match.Id, newMatchId));
        return new RematchOutcome((int)RematchStatus.Created, newMatchId);
    }

    /// <summary>Same shape as <see cref="LiveLobbyGrain.TryCreateDuelAsync"/>: mint a collision-free
    /// code, create the grain with the original challenger, then join the original opponent. Anything
    /// but a clean join is a failed attempt, not a half-open duel.</summary>
    private async Task<string?> CreateRematchDuelAsync(List<Question> set)
    {
        var m = _match!;
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var code = ids.NewMatchCode();
            if (await archive.ByCodeAsync(code) is not null) continue;

            var newMatchId = ids.NewId();
            var grain = GrainFactory.GetGrain<ILiveMatchGrain>(newMatchId);
            await grain.CreateAsync(code, (int)m.Lang, m.ChallengerId, [.. set.Select(q => q.Id)]);

            var joinResult = (LiveJoinResult)await grain.JoinAsync(m.OpponentId!);
            return joinResult == LiveJoinResult.Joined ? newMatchId : null;
        }

        return null;
    }

    private async Task<RematchOutcome> FailRematchAsync()
    {
        state.State.RematchReadyAt.Clear();
        await state.WriteStateAsync();
        await SafeNotifyAsync(() => notifier.RematchFailedAsync(_match!.Id));
        return new RematchOutcome((int)RematchStatus.Failed);
    }

    /// <summary>A readiness flag older than <see cref="LiveRules.RematchExpires"/> no longer counts
    /// towards completing the handshake — a press arriving after it starts the wait over rather than
    /// dragging an absent player into a fresh duel.</summary>
    private void PruneExpiredReadiness(DateTimeOffset now)
    {
        var cutoff = now - LiveRules.RematchExpires;
        foreach (var stale in state.State.RematchReadyAt.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            state.State.RematchReadyAt.Remove(stale);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (_match is null) return;

        var phaseBefore = _match.Phase;
        var wasOver = _match.IsOver;
        _match.Advance(clock.Now);
        await AfterChangeAsync(phaseBefore, wasOver);
    }

    private async Task OnTimerTickAsync(CancellationToken ct)
    {
        if (_match is null || _match.IsOver) return;

        var phaseBefore = _match.Phase;
        var wasOver = _match.IsOver;
        // A wall-clock/TimeProvider rounding race can fire this with nothing yet due; Advance is a
        // no-op then, and AfterChangeAsync still re-arms rather than leaving the duel with no tick.
        _match.Advance(clock.Now);
        await AfterChangeAsync(phaseBefore, wasOver);
    }

    /// <summary>
    /// The one door every state-changing entry point leaves through: persist, re-arm the clock, then
    /// notify. Notification is last on purpose — a notifier that throws leaves the state and the
    /// clock intact.
    /// </summary>
    private async Task AfterChangeAsync(LivePhase phaseBefore, bool wasOver, string? endReason = null)
    {
        await SaveAsync();
        // A duel that is already over is about to have its row removed in NotifyAsync's ending
        // branch below — writing it here first would only be undone a moment later.
        if (!_match!.IsOver) await UpsertDirectoryAsync();
        await RearmAsync();
        await NotifyAsync(phaseBefore, wasOver, endReason);
    }

    private async Task NotifyAsync(LivePhase phaseBefore, bool wasOver, string? endReason)
    {
        var m = _match!;

        if (phaseBefore == LivePhase.Lobby && m.Phase == LivePhase.Countdown)
            await SafeNotifyAsync(() => notifier.CountdownStartedAsync(m.Id, BuildCountdown(m)));

        for (var i = 0; i < m.Rounds.Count; i++)
        {
            if (i >= _startedThrough)
            {
                var round = m.Rounds[i];
                var question = await questions.GetAsync(round.QuestionId);
                if (question is null)
                {
                    // The round already opened in the domain against a question id nothing can
                    // resolve — an operational data problem, not a player's fault. End it rather
                    // than leave the duel wedged with a card nobody can ever build.
                    logger.LogWarning(
                        "Question {QuestionId} for round {Slot} of live duel {MatchId} could not be resolved; ending as no-contest.",
                        round.QuestionId, round.Slot, m.Id);
                    if (!m.IsOver) m.EndNoContest(clock.Now);
                    await SaveAsync();
                    await RearmAsync();
                    break;
                }

                var category = await categories.GetAsync(question.CategoryId);
                await SafeNotifyAsync(() => notifier.RoundStartedAsync(m.Id, BuildRoundCard(round, question, category, m.QuestionIds.Count)));
                _startedThrough = i + 1;
            }

            if (i < ClosedRoundCount(m) && i >= _revealedThrough)
            {
                var reveal = await BuildRoundRevealAsync(m, i);
                await SafeNotifyAsync(() => notifier.RoundRevealedAsync(m.Id, reveal));
                _revealedThrough = i + 1;
            }
        }

        if (!wasOver && m.IsOver)
        {
            await IndexAsync(); // the row has to read NoContest/Resolved/Abandoned before anyone can be told
            await RemoveFromDirectoryAsync(); // a finished duel never lingers in the in-flight list
            await SettleAsync();
            await SafeNotifyAsync(() => notifier.EndedAsync(m.Id, BuildEnded(m, endReason)));
        }
    }

    /// <summary>Everything that happens once, when a duel ends: history, stats, leaderboard.
    /// TODO(live-duel settling sub-issue): archive the duel, update the leaderboard, and call
    /// IPlayerGrain.ApplyResultAsync for both players. Out of scope here — this grain only ends
    /// the duel and exposes the outcome.</summary>
    private Task SettleAsync() => Task.CompletedTask;

    private async Task RearmAsync()
    {
        _timer?.Dispose();
        _timer = null;

        if (_match is null || _match.IsOver)
        {
            if (await this.GetReminder(SafetyNetReminder) is { } reminder)
                await this.UnregisterReminder(reminder);
            return;
        }

        var due = _match.NextDueAt!.Value - clock.Now;
        if (due < MinimumDueTime) due = MinimumDueTime;

        _timer = this.RegisterGrainTimer(OnTimerTickAsync, new GrainTimerCreationOptions
        {
            DueTime = due,
            Period = Timeout.InfiniteTimeSpan,
            KeepAlive = true
        });

        this.DelayDeactivation(due + DeactivationSlack);
    }

    private async Task SafeNotifyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ILiveNotifier threw for live duel {MatchId}; the duel keeps running.", _match?.Id);
        }
    }

    /// <summary>Writes this duel's row to the in-flight index — everything <c>/admin/live</c> needs
    /// without activating this grain. Called on every phase change while the duel is still running.</summary>
    private Task UpsertDirectoryAsync()
    {
        var m = _match!;
        var row = new LiveDirectoryRow(m.Id, m.Code, m.ChallengerId, m.OpponentId, (int)m.Lang,
            m.CurrentRound?.Slot ?? m.Rounds.Count, m.QuestionIds.Count, (int)m.Phase, m.CreatedAt);
        return SafeDirectoryAsync(() => directory.UpsertAsync(row));
    }

    private Task RemoveFromDirectoryAsync() => SafeDirectoryAsync(() => directory.RemoveAsync(_match!.Id));

    /// <summary>Same treatment as <see cref="SafeNotifyAsync"/>: the index is a nicety for admins, not
    /// something a Redis blip is allowed to wedge the duel over.</summary>
    private async Task SafeDirectoryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ILiveDirectory threw for live duel {MatchId}; the duel keeps running.", _match?.Id);
        }
    }

    private Task SaveAsync()
    {
        state.State.Json = JsonSerializer.Serialize(_match!.ToSnapshot());
        return state.WriteStateAsync();
    }

    /// <summary>Mirrors the duel into Mongo so its code can be resolved and its lifecycle read without
    /// activating the grain — exactly what <c>MatchGrain.IndexAsync</c> does for an async match.
    /// Scores stay 0 here: settling — what a live row's score means — is the settling sub-issue's.</summary>
    private Task IndexAsync()
    {
        var m = _match!;
        return archive.SaveAsync(new ArchivedMatch(m.Id, m.Code, m.Lang, m.ChallengerId, m.OpponentId, m.WinnerId, m.IsDraw,
            0, 0, m.State, m.CreatedAt, m.EndedAt, [.. m.QuestionIds], IsLive: true));
    }

    /// <summary>How many of the current rounds are closed (revealed or done) — every round but the
    /// last while it is still open for answers, or all of them otherwise.</summary>
    private static int ClosedRoundCount(LiveMatch m) => m.Phase == LivePhase.Question ? Math.Max(0, m.Rounds.Count - 1) : m.Rounds.Count;

    private static IEnumerable<string> Participants(LiveMatch m)
    {
        yield return m.ChallengerId;
        if (m.OpponentId is not null) yield return m.OpponentId;
    }

    private static LiveCountdown BuildCountdown(LiveMatch m) => new(m.PhaseEndsAt!.Value, m.ChallengerId, m.OpponentId!, m.QuestionIds.Count);

    private static LiveRoundCard BuildRoundCard(LiveRound round, Question question, Category? category, int totalRounds) => new(
        round.Slot, totalRounds, question.Id, question.Prompt, [.. question.Choices],
        question.CategoryId, category?.NameFor(question.Lang) ?? question.CategoryId,
        category?.Icon ?? "", category?.Color ?? "", question.Level, question.Media,
        round.StartedAt, round.StartedAt + MatchRules.QuestionTime);

    private async Task<LiveRoundReveal> BuildRoundRevealAsync(LiveMatch m, int index)
    {
        var round = m.Rounds[index];
        var question = await questions.GetAsync(round.QuestionId);

        var players = Participants(m).Select(pid =>
        {
            var answer = round.Answers.TryGetValue(pid, out var a) ? a : new LiveAnswer(-1, false, 0, 0);
            var total = m.Rounds.Take(index + 1).Sum(r => r.Answers.TryGetValue(pid, out var ra) ? ra.Score : 0);
            return new LivePlayerRound(pid, answer.ChoiceIndex, answer.Correct, answer.Score, total);
        }).ToList();

        return new LiveRoundReveal(round.Slot, question?.CorrectIndex ?? -1, question?.Explanation, players,
            round.StartedAt + MatchRules.QuestionTime + LiveRules.RevealTime);
    }

    private static LiveEnded BuildEnded(LiveMatch m, string? reason) => new(
        m.State, m.WinnerId, m.IsDraw, m.AbandonedBy,
        [.. Participants(m).Select(pid => new LivePlayerScore(pid, m.Score(pid), CorrectCount(m, pid)))],
        reason);

    private static int CorrectCount(LiveMatch m, string playerId) => m.Rounds.Count(r => r.Answers.TryGetValue(playerId, out var a) && a.Correct);

    /// <summary>
    /// The fairness rule for a reconnecting client: the round in flight never gives up the correct
    /// index, and an opponent's choice appears only once the round has closed.
    /// </summary>
    private async Task<LiveView> ViewAsync(LiveMatch m, string forPlayerId)
    {
        var closedCount = ClosedRoundCount(m);
        var closedIds = m.Rounds.Take(closedCount).Select(r => r.QuestionId).Distinct().ToList();
        var byId = closedIds.Count == 0
            ? new Dictionary<string, Question>()
            : (await questions.GetManyAsync(closedIds)).ToDictionary(q => q.Id);

        var rounds = new List<LiveRoundResultView>();
        for (var i = 0; i < m.Rounds.Count; i++)
        {
            var round = m.Rounds[i];
            var closed = i < closedCount;
            var correctIndex = closed && byId.TryGetValue(round.QuestionId, out var q) ? q.CorrectIndex : (int?)null;

            var answers = Participants(m).Select(pid =>
            {
                var answered = round.HasAnswered(pid);
                var visible = closed || pid == forPlayerId;
                var answer = answered && visible ? round.Answers[pid] : null;
                return new LiveRoundAnswerView(pid, answered, answer?.ChoiceIndex, visible ? answer?.Correct : null, answer?.Score ?? 0);
            }).ToList();

            rounds.Add(new LiveRoundResultView(round.Slot, round.QuestionId, round.StartedAt, correctIndex, answers));
        }

        var players = Participants(m).Select(pid => new LivePlayerView(
            pid,
            m.Rounds.Take(closedCount).Sum(r => r.Answers.TryGetValue(pid, out var a) ? a.Score : 0),
            m.Rounds.Take(closedCount).Count(r => r.Answers.TryGetValue(pid, out var a) && a.Correct),
            m.MissStreak(pid))).ToList();

        return new LiveView(
            m.Id, m.ChallengerId, m.OpponentId, (int)m.State, (int)m.Phase, m.PhaseEndsAt,
            m.CurrentRound?.Slot ?? m.Rounds.Count, m.QuestionIds.Count, players, rounds,
            m.WinnerId, m.IsDraw, m.AbandonedBy, m.CreatedAt, m.EndedAt, m.Code, (int)m.Lang);
    }
}
