using Microsoft.Extensions.Logging;
using System.Text.Json;
using Orleans;
using Quesshi.Grains.Abstractions;
using Orleans.Runtime;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Grains;

public sealed class MatchGrain(
    [PersistentState("match", "hot")] IPersistentState<MatchStateRecord> state,
    IQuestionRepository questions,
    IMatchArchive archive,
    QuestionSetBuilder questionSetBuilder,
    IClock clock,
    ILiveNotifier notifier,
    ILogger<MatchGrain> logger) : Grain, IMatchGrain, IRemindable
{
    private const string ForfeitReminder = "forfeit";
    private Match? _match;

    /// <summary>
    /// Durable settlement progress, kept as its own hand-managed JSON string on
    /// <see cref="MatchStateRecord.SettlementJson"/> rather than as plain fields on this record, for
    /// the reason spelled out there: it has to be possible to tell "never written" apart from "written
    /// with false" no matter how the outer state object happens to be serialised. <see cref="Complete"/>
    /// is false while settlement is running or has only partly finished, and true once every
    /// participant has been settled; <see cref="SettledPlayers"/> is exactly who is done so far.
    /// </summary>
    private sealed record SettlementProgress(bool Complete, List<string> SettledPlayers);

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(state.State.Json))
            _match = Match.FromSnapshot(JsonSerializer.Deserialize<MatchSnapshot>(state.State.Json)!);

        // Resume an interrupted settlement before this activation serves anything else — the async
        // history listing (GameEndpoints.cs) reactivates exactly this kind of grain for archived rows,
        // so "wait for someone to reopen the match" is not a real retry trigger for most of them.
        // A match that is over with a settlement block present and not yet Complete was mid-effect
        // when the process died or a write failed; PlayerGrain's own per-(match, player) dedup marker
        // (TryRecordSettledMatch) makes replaying every participant safe, so this just finishes what
        // was interrupted. A record with no settlement block at all (LoadProgress() returns null) was
        // necessarily written by code that predates this field entirely — see the tri-state note on
        // MatchStateRecord.SettlementJson — and must never be settled here or anywhere else; every
        // match that has ever finished, going back to before this feature existed, is still sitting in
        // Redis in exactly that shape, and settling it again would double-apply its stats and
        // leaderboard contribution the first time anyone merely opens their duel history.
        if (_match is { IsOver: true } && LoadProgress() is { Complete: false })
            await SettleAsync();
    }

    public async Task<MatchView> CreateAsync(int lang, string challengerId, List<string> questionIds, string code)
    {
        if (_match is not null) return View(_match, challengerId);

        // What the deleted pre-lobby Create overload used to do internally, inlined here instead: a
        // capacity-2 lobby whose set is already drawn, built through the settings-aware constructor so
        // the domain itself never has to carry a second, N-unaware way to come into being.
        var settings = DuelSettings.Create((Language)lang, questionIds.Count, [], []);
        _match = Match.Create(this.GetPrimaryKeyString(), code, challengerId, settings, capacity: 2, clock.Now);
        _match.DrawQuestions(questionIds);
        await SaveAsync();
        await IndexAsync();

        // Orleans insists on a period; we unregister as soon as it fires or the match ends.
        await this.RegisterOrUpdateReminder(ForfeitReminder, MatchRules.ForfeitAfter, TimeSpan.FromHours(6));
        return View(_match, challengerId);
    }

    /// <summary>The lobby-aware create path: see the interface's own remarks. Shares everything past
    /// construction with <see cref="CreateAsync"/> — only how the domain object itself is built differs.</summary>
    public async Task<MatchView> CreateLobbyAsync(string code, string ownerId, int lang, int questionCount,
        List<string> categoryIds, List<int> levels, int capacity)
    {
        if (_match is not null) return View(_match, ownerId);

        var settings = DuelSettings.Create((Language)lang, questionCount, categoryIds, [.. levels.Select(l => (Difficulty)l)]);
        _match = Match.Create(this.GetPrimaryKeyString(), code, ownerId, settings, capacity, clock.Now);
        await SaveAsync();
        await IndexAsync();

        await this.RegisterOrUpdateReminder(ForfeitReminder, MatchRules.ForfeitAfter, TimeSpan.FromHours(6));
        return View(_match, ownerId);
    }

    public async Task<bool> JoinAsync(string playerId)
    {
        if (_match is null) return false;
        if (_match.IsParticipant(playerId)) return true;

        try
        {
            _match.Join(playerId, clock.Now);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        await SaveAsync();
        await IndexAsync();
        await SafeNotifyAsync(() => notifier.LobbyUpdatedAsync(_match.Id));
        return true;
    }

    /// <summary>Draws this lobby's question set from its own <c>Settings</c> and hands it to
    /// <see cref="Match.DrawQuestions"/> — the one bit of IO the domain cannot do for itself. Shared by
    /// <see cref="JoinAsync"/>'s auto-start branch and <see cref="StartAsync"/>.</summary>
    private async Task DrawQuestionsAsync()
    {
        var m = _match!;
        var set = await questionSetBuilder.BuildAsync(m.Settings.Language, m.Settings.CategoryIds, m.Settings.QuestionCount, m.Settings.Levels);
        m.DrawQuestions([.. set.Select(q => q.Id)]);
    }

    public async Task<bool> StartAsync(string playerId)
    {
        if (_match is null || _match.State != MatchState.AwaitingOpponent) return false;
        if (playerId != _match.OwnerId || _match.Participants.Count < 2) return false;

        if (_match.QuestionIds.Count == 0) await DrawQuestionsAsync();
        if (!_match.Start(playerId, clock.Now)) return false;

        await SaveAsync();
        await IndexAsync();
        await SafeNotifyAsync(() => notifier.LobbyUpdatedAsync(_match.Id));
        return true;
    }

    /// <summary>The random-matchmaking counterpart to <see cref="StartAsync"/>: see the interface's
    /// own remarks. Draws the question set first, exactly as <see cref="StartAsync"/> does, since
    /// neither pairing site draws it themselves.</summary>
    public async Task<bool> StartPairedAsync()
    {
        if (_match is null || _match.State != MatchState.AwaitingOpponent) return false;

        if (_match.QuestionIds.Count == 0) await DrawQuestionsAsync();
        if (!_match.StartByPairing(clock.Now)) return false;

        await SaveAsync();
        await IndexAsync();
        await SafeNotifyAsync(() => notifier.LobbyUpdatedAsync(_match.Id));
        return true;
    }

    public async Task<bool> LeaveAsync(string playerId)
    {
        if (_match is null) return false;

        var wasOver = _match.IsOver;
        var ok = playerId == _match.OwnerId ? _match.Cancel(playerId, clock.Now) : _match.Leave(playerId, clock.Now);
        if (!ok) return false;

        await SaveAsync();
        await IndexAsync();
        await SafeNotifyAsync(() => notifier.LobbyUpdatedAsync(_match.Id));

        // A cancelled lobby is over the instant Cancel succeeds; a freed seat never is. Mirrors
        // AnswerAsync's own "!wasOver && IsOver" trigger, so the reminder gets unregistered and the
        // archive row reads NoContest rather than being left to a forfeit tick that will never come
        // (a cancelled lobby has already left AwaitingOpponent, so TryForfeit can no longer reach it).
        if (!wasOver && _match.IsOver) await SettleAsync();
        return true;
    }

    /// <summary>
    /// Atomically updates settings, capacity, or both — mirrors <c>LiveMatchGrain.UpdateSettingsAsync</c>
    /// exactly, including the equal-settings no-op rule and settling the clock (here, <see cref="Match.TryForfeit"/>)
    /// once, up front, before either half is evaluated — and reusing that one <c>now</c> for every
    /// clock-facing call below, since <see cref="Match.SetCapacity"/> settles the clock again itself and
    /// a second, later <c>clock.Now</c> read here could cross the match's forfeit deadline between the
    /// pre-check and the apply, silently discarding an already-approved capacity change.
    /// </summary>
    public async Task<bool> UpdateSettingsAsync(string playerId, int lang, int questionCount, List<string> categoryIds, List<int> levels, int? capacity)
    {
        if (_match is null) return false;

        DuelSettings settings;
        try
        {
            settings = DuelSettings.Create((Language)lang, questionCount, categoryIds, [.. levels.Select(l => (Difficulty)l)]);
        }
        catch (ArgumentException)
        {
            return false; // an invalid combination refuses the change outright, same as at creation
        }

        var now = clock.Now;
        var wasOver = _match.IsOver;
        _match.TryForfeit(now);

        var settingsChanged = settings != _match.Settings;
        var settingsOk = !settingsChanged || (playerId == _match.OwnerId && _match.QuestionIds.Count == 0);
        var capacityOk = capacity is not { } newCapacity || _match.CanSetCapacity(playerId, newCapacity);

        if (!settingsOk || !capacityOk)
        {
            await SaveAsync(); // persist whatever TryForfeit just settled, even though refused
            if (!wasOver && _match.IsOver) await SettleAsync();
            return false;
        }

        // Apply half, both checked against the return value: CanSetCapacity/settingsOk were evaluated
        // against the state TryForfeit(now) just settled into, and nothing between here and the apply
        // calls can change that state again — but discarding either's own result would silently claim
        // success for a half that the domain itself refused.
        var settingsApplied = !settingsChanged || _match.UpdateSettings(playerId, settings);
        var capacityApplied = capacity is not { } toApply || _match.SetCapacity(playerId, toApply, now);

        await SaveAsync();
        if (!wasOver && _match.IsOver) await SettleAsync();
        if (!settingsApplied || !capacityApplied) return false;

        await SafeNotifyAsync(() => notifier.LobbyUpdatedAsync(_match.Id));
        return true;
    }

    /// <summary>
    /// Mirrors <c>LiveMatchGrain</c>'s own helper of the same name exactly: a notifier failure — the
    /// hub down, a transient SignalR error — must never fail the mutation that already succeeded and
    /// was already persisted above. The lobby page falls back to its own polling-free "nothing pushed
    /// recently" state until the next successful push, rather than the whole request failing for a
    /// reason that has nothing to do with whether the join/leave/settings-change/start itself worked.
    /// </summary>
    private async Task SafeNotifyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ILiveNotifier threw for async duel {MatchId}; the duel keeps running.", _match?.Id);
        }
    }

    public async Task<ServedSlot?> ServeNextAsync(string playerId)
    {
        if (_match is null || _match.IsOver) return null;
        if (_match.RunOf(playerId)?.Finished == true) return null;

        try
        {
            var served = _match.ServeNext(playerId, clock.Now);
            await SaveAsync();
            return new ServedSlot(served.Index, served.QuestionId, (int)MatchRules.QuestionTime.TotalSeconds, _match.QuestionIds.Count);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "ServeNext refused for {Player} on {Match}", playerId, this.GetPrimaryKeyString());
            return null;
        }
    }

    /// <summary>
    /// Grades and records one answer.
    /// <para>
    /// The grading is <see cref="SubmittedAnswer.TryGrade"/>'s, and the whole reason it is a call
    /// rather than an expression here is the guard this line used to be:
    /// <c>choiceIndex >= 0 &amp;&amp; question.IsCorrect(choiceIndex)</c>. That <c>&gt;= 0</c> is the
    /// timeout check, and a sorting or map answer arrives at exactly -1 — so left in front of the
    /// per-kind branch it would have marked every one of them wrong, silently and for every player.
    /// The per-kind branch therefore comes first, and the timeout check survives inside the choice
    /// branch where it always belonged.
    /// </para>
    /// <para>
    /// A refused submission — a sorting order that is not a permutation, a map answer that does not
    /// parse — throws before anything is recorded, so the run is left exactly as it was and the
    /// endpoint turns it into the same 400 a bad choice index gets. <c>bad_response</c> rather than
    /// prose because that is what the endpoint's own <c>bad_choice</c> reads like, and a client has
    /// to be able to tell the two apart from the outside.
    /// </para>
    /// </summary>
    public async Task<AnswerOutcome> AnswerAsync(string playerId, int slot, int choiceIndex, string? response = null)
    {
        if (_match is null) throw new InvalidOperationException("No such match.");

        var question = await questions.GetAsync(_match.QuestionIds[slot])
            ?? throw new InvalidOperationException("That question has disappeared.");

        if (!SubmittedAnswer.TryGrade(question, _match.Id, slot, choiceIndex, response, out var graded))
            throw new InvalidOperationException("bad_response");

        var correct = graded.Correct;
        var wasOver = _match.IsOver;
        var answer = _match.SubmitAnswer(playerId, slot, graded.ChoiceIndex, correct, clock.Now, question.Level, graded.Response);
        await SaveAsync();

        question.RecordServed(correct);
        await questions.UpsertAsync(question);

        if (!wasOver && _match.IsOver) await SettleAsync();

        var run = _match.RunOf(playerId)!;

        // CorrectIndex keeps its old meaning and keeps it for Choice alone; the other two kinds each
        // get the field that can hold their answer, and Kind says which one the caller should read.
        // A sorting question's correct order is its stored Choices — no seed is consulted here, and
        // none is needed: the shuffle only ever decided how the items were laid out on the card.
        return new AnswerOutcome(correct, question.CorrectIndex, answer.Score, question.Explanation, run.Finished, run.Score,
            (int)question.Kind,
            question.Kind == QuestionKind.Sort ? [.. question.Choices] : null,
            question.Target?.ToResponse());
    }

    public Task<MatchView?> GetAsync(string forPlayerId)
        => Task.FromResult(_match is null ? null : View(_match, forPlayerId));

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (_match is null) return;

        if (_match.TryForfeit(clock.Now))
            await SaveAsync();

        // Fires on a fresh forfeit above and, just as importantly, on every later tick of a match that
        // was already over but never finished settling — nothing else brings a finished duel nobody
        // reopens back to life, which is the entire reason the reminder is unregistered on settlement
        // completion (inside SettleAsync) rather than as soon as the match goes over.
        if (_match.IsOver && LoadProgress() is { Complete: false })
            await SettleAsync();
    }

    /// <summary>
    /// Everything that happens once, when a match ends: history, stats, the leaderboard. Called from
    /// three places that can each observe a different point in an interrupted settlement — the moment
    /// the match itself first goes over (<see cref="AnswerAsync"/>, a fresh forfeit in
    /// <see cref="ReceiveReminder"/>), a later reminder tick retrying one that did not finish, and
    /// <see cref="OnActivateAsync"/> resuming after a crash or reactivation — rather than three
    /// variants, because "resume" and "run for the first time" are the same operation once the block
    /// <see cref="SaveAsync"/> writes the instant a match goes over is treated as the starting point
    /// either way.
    /// </summary>
    private async Task SettleAsync()
    {
        var m = _match!;
        await IndexAsync();

        // Never actually null by the time a legitimate caller reaches this line: SaveAsync writes this
        // block in the very same persisted write that first marks the match over, so every one of the
        // three call sites above either just ran SaveAsync itself or (OnActivateAsync) already checked
        // LoadProgress() is non-null before calling in here. The fallback exists only so a bug
        // elsewhere fails safe into "start from scratch" rather than throwing on a null-reference.
        var progress = LoadProgress() ?? new SettlementProgress(false, []);
        if (progress.Complete) return;

        var settled = new HashSet<string>(progress.SettledPlayers);

        // A cancelled lobby has nothing to settle: LeaveAsync's Cancel path leaves WinnerId null and
        // IsDraw false for a reason that has nothing to do with anyone's run ("nobody in first place",
        // not "everybody lost") — scoring that as a Loss would penalise a lone owner who had already
        // served themself some questions before cancelling. Mirrors LiveMatchSettlement's identical
        // guard for its own NoContest. Added here for the first time: before LeaveAsync/Cancel, an
        // async match could never actually reach NoContest, so this branch was unreachable.
        if (m.State != MatchState.NoContest)
        {
            var byId = (await questions.GetManyAsync(m.QuestionIds)).ToDictionary(q => q.Id);
            var categories = m.QuestionIds.Select(id => byId.GetValueOrDefault(id)?.CategoryId ?? "unknown").ToList();

            foreach (var (playerId, run) in Sides(m))
            {
                if (run is null || settled.Contains(playerId)) continue;

                var outcome = m.IsDraw ? MatchOutcome.Draw : (m.WinnerId == playerId ? MatchOutcome.Win : MatchOutcome.Loss);
                var correct = run.Answers.Select(a => a.Correct).ToList();
                var answeredCategories = categories.Take(correct.Count).ToList();

                // No abandonment on the async path: TryForfeit ends the match but does not distinguish a
                // quitter from an ordinary loser the way a live duel's abandonment does, so abandonedAt is
                // always null here. The grain owns nothing else — PlayerGrain applies the result, and the
                // guest exclusion and the unconditional leaderboard projection are its job, not this one's.
                //
                // If this throws — a real or simulated storage failure inside PlayerGrain — it propagates
                // straight out of SettleAsync without touching `settled` or the checkpoint below, so a
                // retry (the next reminder tick, or the next activation) attempts this exact player again
                // rather than silently skipping them as done.
                await GrainFactory.GetGrain<IPlayerGrain>(playerId).SettleMatchAsync(m.Id, (int)outcome, run.Score, answeredCategories, correct, null);

                settled.Add(playerId);

                // Checkpointed after every participant's effect, not once at the end: a crash between two
                // participants leaves exactly the one already applied on record, so a resume only ever
                // re-attempts whoever it never actually finished. PlayerGrain's own per-(match, player)
                // dedup marker is what actually makes a repeated attempt for the same player safe — this
                // checkpoint only saves re-deriving and re-applying an effect that dedup would have turned
                // into a no-op anyway, which is why losing this exact write can no longer corrupt anything.
                await SaveProgressAsync(new SettlementProgress(false, [.. settled]));
            }
        }

        await SaveProgressAsync(new SettlementProgress(true, [.. settled]));

        // Unregistered on completion, not on IsOver: a transient failure above must leave this
        // reminder alive as the retry trigger this whole design exists for, so a duel nobody reopens
        // still finishes settling the next time it fires, rather than depending on a player opening a
        // match that, from their side, already looks resolved.
        if (await this.GetReminder(ForfeitReminder) is { } registered)
            await this.UnregisterReminder(registered);
    }

    /// <summary>Mirrors the match into Mongo so it can be listed and found by code; grains cannot be enumerated.</summary>
    private Task IndexAsync()
    {
        var m = _match!;

        // ArchivedMatch.ChallengerId/OpponentId are its own permanent two-scalar fields — kept for
        // MatchDoc's legacy shape and the readers that still switch on it — not the domain's deleted
        // compatibility accessors of the same names: this is Participants[0] and, when seated,
        // Participants[1], read directly now that Match no longer offers them as a shortcut.
        var opponentId = m.Participants.Count > 1 ? m.Participants[1] : null;
        return archive.SaveAsync(new ArchivedMatch(m.Id, m.Code, m.Lang, m.Participants[0], opponentId, m.WinnerId, m.IsDraw,
            BuildResults(m), m.State, m.CreatedAt, m.EndedAt, [.. m.QuestionIds]));
    }

    /// <summary>
    /// The archive's per-participant row: the real ranked <c>Standing</c>s once the match is over, or
    /// each side's currently banked score with the "not yet ranked" placeholder while it is still being
    /// played — exactly what <c>ChallengerScore</c>/<c>OpponentScore</c> always showed here before
    /// <see cref="ParticipantResult"/> replaced them, since this method is called from <c>JoinAsync</c>
    /// and <c>CreateAsync</c> too, long before the match is decided.
    /// </summary>
    private static List<ParticipantResult> BuildResults(Match m)
    {
        var byId = m.Standings.ToDictionary(s => s.PlayerId);
        return [.. Sides(m).Select(s => byId.TryGetValue(s.PlayerId, out var standing)
            ? new ParticipantResult(s.PlayerId, s.Run?.Score ?? 0, standing.Place, standing.Outcome)
            : new ParticipantResult(s.PlayerId, s.Run?.Score ?? 0, 0, MatchOutcome.Loss))];
    }

    /// <summary>
    /// Every seated player, not just the first two: <see cref="Match.Participants"/> directly, rather
    /// than the obsolete <c>ChallengerId</c>/<c>OpponentId</c> pair this used to yield. A
    /// capacity-&gt;2 async lobby is fully N-player at the domain level already — <see cref="Match"/>'s
    /// own <c>BuildStandings</c> ranks every participant, not just two — so this was the one place
    /// still truncating that back down to two on the way out to a grain caller: <see cref="View"/>'s
    /// <c>Runs</c>, <see cref="BuildResults"/>'s archived row, and <see cref="SettleAsync"/>'s
    /// per-player settlement loop all read this. The identical bug, for the identical reason, that
    /// <c>LiveMatchGrain</c>'s own <c>Participants</c> helper was fixed for on the live side.
    /// </summary>
    private static IEnumerable<(string PlayerId, PlayerRun? Run)> Sides(Match m) =>
        m.Participants.Select(pid => (pid, m.RunOf(pid)));

    private SettlementProgress? LoadProgress()
        => string.IsNullOrEmpty(state.State.SettlementJson)
            ? null
            : JsonSerializer.Deserialize<SettlementProgress>(state.State.SettlementJson);

    private Task SaveProgressAsync(SettlementProgress progress)
    {
        state.State.SettlementJson = JsonSerializer.Serialize(progress);
        return state.WriteStateAsync();
    }

    private Task SaveAsync()
    {
        state.State.Json = JsonSerializer.Serialize(_match!.ToSnapshot());

        // The instant a match is first observed to be over, the settlement block is written in this
        // very same persisted write — never a separate, later one — closing the one gap that would
        // otherwise make a freshly-ended match indistinguishable from a genuinely historical one:
        // absent must mean "settled by code that predates this field entirely," and a match ending
        // under this code must never be able to produce that value, including if the process dies a
        // moment later. Guarded on LoadProgress() so an already-started block — still running, or long
        // finished — is never clobbered by an unrelated save later in the match's life (ServeNext and
        // Answer both call this helper long after a match is over is no longer possible, but staying
        // guarded costs nothing and keeps this helper correct regardless of who calls it next).
        if (_match!.IsOver && LoadProgress() is null)
            state.State.SettlementJson = JsonSerializer.Serialize(new SettlementProgress(false, []));

        return state.WriteStateAsync();
    }

    /// <summary>
    /// The fairness rule lives here and nowhere else: until you have finished your own run,
    /// the other player's answers are not in the object you receive.
    /// </summary>
    private static MatchView View(Match m, string forPlayerId)
    {
        var reveal = m.CanReveal(forPlayerId) || m.IsOver;

        var runs = Sides(m)
            .Where(s => s.Run is not null)
            .Select(s => new RunView(s.PlayerId, s.Run!.Score, s.Run.Correct, s.Run.Answers.Count, s.Run.Finished,
                s.PlayerId == forPlayerId || reveal
                    ? [.. s.Run.Answers.Select(a => a.ChoiceIndex)]
                    : [],
                // Sorting and map answers are hidden by the same test in the same expression as the
                // choice indices beside them, rather than by a rule of their own: a sort answer left
                // visible early leaks precisely what an early choice index does, and two rules that
                // have to be kept in step are one rule waiting to fall out of step.
                s.PlayerId == forPlayerId || reveal
                    ? [.. s.Run.Answers.Select(a => a.Response)]
                    : []))
            .ToList();

        // Scores of an unfinished opponent are hidden too, or the reveal leaks through arithmetic.
        if (!reveal)
            runs = [.. runs.Select(r => r.PlayerId == forPlayerId ? r : r with { Score = 0, Correct = 0 })];

        return new MatchView(m.Id, m.Code, (int)m.Lang, [.. m.Participants], (int)m.State, m.WinnerId, m.IsDraw,
            m.CreatedAt, [.. m.QuestionIds], runs, m.Capacity, m.Settings.QuestionCount,
            [.. m.Settings.CategoryIds], [.. m.Settings.Levels.Select(l => (int)l)]);
    }
}
