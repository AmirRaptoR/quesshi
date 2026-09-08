using Microsoft.Extensions.Logging;
using System.Text.Json;
using Orleans;
using Quesshi.Grains.Abstractions;
using Orleans.Runtime;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Grains;

public sealed class MatchGrain(
    [PersistentState("match", "hot")] IPersistentState<MatchStateRecord> state,
    IQuestionRepository questions,
    IMatchArchive archive,
    IClock clock,
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

        _match = Match.Create(this.GetPrimaryKeyString(), code, (Language)lang, challengerId, questionIds, clock.Now);
        await SaveAsync();
        await IndexAsync();

        // Orleans insists on a period; we unregister as soon as it fires or the match ends.
        await this.RegisterOrUpdateReminder(ForfeitReminder, MatchRules.ForfeitAfter, TimeSpan.FromHours(6));
        return View(_match, challengerId);
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
        return true;
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

    public async Task<AnswerOutcome> AnswerAsync(string playerId, int slot, int choiceIndex)
    {
        if (_match is null) throw new InvalidOperationException("No such match.");

        var question = await questions.GetAsync(_match.QuestionIds[slot])
            ?? throw new InvalidOperationException("That question has disappeared.");

        var correct = choiceIndex >= 0 && question.IsCorrect(choiceIndex);
        var wasOver = _match.IsOver;
        var answer = _match.SubmitAnswer(playerId, slot, choiceIndex, correct, clock.Now, question.Level);
        await SaveAsync();

        question.RecordServed(correct);
        await questions.UpsertAsync(question);

        if (!wasOver && _match.IsOver) await SettleAsync();

        var run = _match.RunOf(playerId)!;
        return new AnswerOutcome(correct, question.CorrectIndex, answer.Score, question.Explanation, run.Finished, run.Score);
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
        return archive.SaveAsync(new ArchivedMatch(m.Id, m.Code, m.Lang, m.ChallengerId, m.OpponentId, m.WinnerId, m.IsDraw,
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

    private static IEnumerable<(string PlayerId, PlayerRun? Run)> Sides(Match m)
    {
        yield return (m.ChallengerId, m.RunOf(m.ChallengerId));
        if (m.OpponentId is not null) yield return (m.OpponentId, m.RunOf(m.OpponentId));
    }

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
                    : []))
            .ToList();

        // Scores of an unfinished opponent are hidden too, or the reveal leaks through arithmetic.
        if (!reveal)
            runs = [.. runs.Select(r => r.PlayerId == forPlayerId ? r : r with { Score = 0, Correct = 0 })];

        return new MatchView(m.Id, m.Code, (int)m.Lang, m.ChallengerId, m.OpponentId, (int)m.State, m.WinnerId, m.IsDraw,
            m.CreatedAt, [.. m.QuestionIds], runs);
    }
}
