namespace Quesshi.Domain;

/// <summary>
/// A duel between two players over the same questions. Pure state machine: no storage, no clock,
/// no Orleans. Every method that needs "now" is handed it, which is what makes the rules testable.
/// </summary>
public sealed class Match
{
    private readonly Dictionary<string, PlayerRun> _runs = [];
    private readonly List<string> _questionIds;

    private Match(string id, string code, Language lang, string challengerId, IEnumerable<string> questionIds, DateTimeOffset createdAt)
    {
        Id = id;
        Code = code;
        Lang = lang;
        ChallengerId = challengerId;
        _questionIds = [.. questionIds];
        CreatedAt = createdAt;
    }

    public string Id { get; }
    public string Code { get; }
    public Language Lang { get; }
    public string ChallengerId { get; }
    public string? OpponentId { get; private set; }
    public IReadOnlyList<string> QuestionIds => _questionIds;
    public MatchState State { get; private set; } = MatchState.AwaitingOpponent;
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string? WinnerId { get; private set; }
    public bool IsDraw { get; private set; }

    public bool IsOver => State is MatchState.Resolved or MatchState.Forfeited;

    public static Match Create(string id, string code, Language lang, string challengerId, IReadOnlyList<string> questionIds, DateTimeOffset now)
    {
        if (!MatchRules.IsValidCount(questionIds.Count))
            throw new ArgumentException(
                $"A match needs one of {string.Join(", ", MatchRules.QuestionCountChoices)} questions, got {questionIds.Count}.", nameof(questionIds));
        if (questionIds.Distinct().Count() != questionIds.Count)
            throw new ArgumentException("A match cannot repeat a question.", nameof(questionIds));

        return new Match(id, code, lang, challengerId, questionIds, now);
    }

    public void Join(string playerId, DateTimeOffset now)
    {
        if (playerId == ChallengerId) throw new InvalidOperationException("You cannot join your own challenge.");
        if (State != MatchState.AwaitingOpponent) throw new InvalidOperationException("This challenge has already been taken.");

        OpponentId = playerId;
        State = MatchState.InProgress;
    }

    public bool IsParticipant(string playerId) => playerId == ChallengerId || playerId == OpponentId;

    public PlayerRun? RunOf(string playerId) => _runs.GetValueOrDefault(playerId);

    /// <summary>You may see the other side only once your own run is done.</summary>
    public bool CanReveal(string playerId) => RunOf(playerId)?.Finished == true;

    public ServedQuestion ServeNext(string playerId, DateTimeOffset now)
    {
        RequireParticipant(playerId);
        if (IsOver) throw new InvalidOperationException("This match is over.");

        var run = _runs.TryGetValue(playerId, out var existing) ? existing : _runs[playerId] = new PlayerRun(_questionIds.Count);
        if (run.Finished) throw new InvalidOperationException("You have already finished your run.");

        run.MarkServed(now);
        return new ServedQuestion(run.NextSlot, _questionIds[run.NextSlot], now);
    }

    public AnswerRecord SubmitAnswer(string playerId, int slot, int choiceIndex, bool correct, DateTimeOffset now,
        Difficulty level = Difficulty.Medium)
    {
        RequireParticipant(playerId);

        if (!_runs.TryGetValue(playerId, out var run) || run.ServedAt is not { } servedAt)
            throw new InvalidOperationException("That question was never served to you.");
        if (slot != run.NextSlot)
            throw new InvalidOperationException($"Expected an answer for question {run.NextSlot}, got {slot}.");

        var taken = now - servedAt;
        var answer = new AnswerRecord(slot, choiceIndex, correct, Scoring.Score(correct, taken, MatchRules.QuestionTime, level), taken.TotalSeconds);
        run.Record(answer);

        TryResolve(now);
        return answer;
    }

    /// <summary>Ends a match that has sat untouched past the deadline. Returns false if it was not due.</summary>
    public bool TryForfeit(DateTimeOffset now)
    {
        if (IsOver) return false;
        if (now < CreatedAt + MatchRules.ForfeitAfter) return false;

        Finish(MatchState.Forfeited, now);
        return true;
    }

    public MatchSnapshot ToSnapshot() => new(
        Id, Code, Lang, ChallengerId, OpponentId, [.. _questionIds], State, CreatedAt, EndedAt, WinnerId, IsDraw,
        _runs.ToDictionary(kv => kv.Key, kv => new RunSnapshot([.. kv.Value.Answers], kv.Value.ServedAt)));

    public static Match FromSnapshot(MatchSnapshot s)
    {
        var m = new Match(s.Id, s.Code, s.Lang, s.ChallengerId, s.QuestionIds, s.CreatedAt)
        {
            OpponentId = s.OpponentId,
            State = s.State,
            EndedAt = s.EndedAt,
            WinnerId = s.WinnerId,
            IsDraw = s.IsDraw
        };
        foreach (var (playerId, run) in s.Runs)
            m._runs[playerId] = PlayerRun.Restore(s.QuestionIds.Count, run.Answers, run.ServedAt);
        return m;
    }

    private void TryResolve(DateTimeOffset now)
    {
        if (OpponentId is null) return;
        if (RunOf(ChallengerId)?.Finished != true || RunOf(OpponentId)?.Finished != true) return;

        Finish(MatchState.Resolved, now);
    }

    private void Finish(MatchState state, DateTimeOffset now)
    {
        State = state;
        EndedAt = now;

        var mine = RunOf(ChallengerId)?.Score ?? 0;
        var theirs = OpponentId is null ? 0 : RunOf(OpponentId)?.Score ?? 0;

        IsDraw = mine == theirs;
        WinnerId = IsDraw ? null : (mine > theirs ? ChallengerId : OpponentId);
    }

    private void RequireParticipant(string playerId)
    {
        if (!IsParticipant(playerId)) throw new InvalidOperationException("You are not in this match.");
    }
}
