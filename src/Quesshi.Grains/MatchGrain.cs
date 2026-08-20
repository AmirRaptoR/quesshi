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
    IPlayerRepository players,
    ILeaderboard leaderboard,
    IClock clock,
    ILogger<MatchGrain> logger) : Grain, IMatchGrain, IRemindable
{
    private const string ForfeitReminder = "forfeit";
    private Match? _match;

    public override Task OnActivateAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(state.State.Json))
            _match = Match.FromSnapshot(JsonSerializer.Deserialize<MatchSnapshot>(state.State.Json)!);
        return Task.CompletedTask;
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
        var answer = _match.SubmitAnswer(playerId, slot, choiceIndex, correct, clock.Now);
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
        {
            await SaveAsync();
            await SettleAsync();
        }

        if (_match.IsOver && await this.GetReminder(ForfeitReminder) is { } registered)
            await this.UnregisterReminder(registered);
    }

    /// <summary>Everything that happens once, when a match ends: history, stats, leaderboard.</summary>
    private async Task SettleAsync()
    {
        var m = _match!;
        await IndexAsync();

        var byId = (await questions.GetManyAsync(m.QuestionIds)).ToDictionary(q => q.Id);
        var categories = m.QuestionIds.Select(id => byId.GetValueOrDefault(id)?.CategoryId ?? "unknown").ToList();

        foreach (var (playerId, run) in Sides(m))
        {
            if (run is null) continue;

            var outcome = m.IsDraw ? MatchOutcome.Draw : (m.WinnerId == playerId ? MatchOutcome.Win : MatchOutcome.Loss);
            var correct = run.Answers.Select(a => a.Correct).ToList();
            var answeredCategories = categories.Take(correct.Count).ToList();

            await GrainFactory.GetGrain<IPlayerGrain>(playerId).ApplyResultAsync((int)outcome, run.Score, answeredCategories, correct);

            // A guest keeps their own result but stays off the board: a throwaway name typed once
            // should not be able to take a rank from someone playing under their own.
            if ((await players.GetAsync(playerId))?.IsGuest != true)
                await leaderboard.AddAsync(playerId, run.Score);
        }
    }

    /// <summary>Mirrors the match into Mongo so it can be listed and found by code; grains cannot be enumerated.</summary>
    private Task IndexAsync()
    {
        var m = _match!;
        return archive.SaveAsync(new ArchivedMatch(m.Id, m.Code, m.Lang, m.ChallengerId, m.OpponentId, m.WinnerId, m.IsDraw,
            m.RunOf(m.ChallengerId)?.Score ?? 0,
            m.OpponentId is null ? 0 : m.RunOf(m.OpponentId)?.Score ?? 0,
            m.State, m.CreatedAt, m.EndedAt, [.. m.QuestionIds]));
    }

    private static IEnumerable<(string PlayerId, PlayerRun? Run)> Sides(Match m)
    {
        yield return (m.ChallengerId, m.RunOf(m.ChallengerId));
        if (m.OpponentId is not null) yield return (m.OpponentId, m.RunOf(m.OpponentId));
    }

    private Task SaveAsync()
    {
        state.State.Json = JsonSerializer.Serialize(_match!.ToSnapshot());
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
