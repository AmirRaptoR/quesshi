namespace Quesshi.Domain;

/// <summary>
/// A live duel: two players on the same question at the same time, on a shared clock. Pure state
/// machine, like <see cref="Match"/> — no storage, no Orleans, no clock of its own. Every method
/// that needs "now" is handed it, and <see cref="Advance"/> is the only one that reads it to decide
/// what changed, so the timer and every answer can drive the same state through the same door.
/// </summary>
public sealed class LiveMatch
{
    private readonly List<string> _questionIds;
    private readonly List<LiveRound> _rounds = [];
    private readonly Dictionary<string, int> _missStreak = [];

    private LiveMatch(string id, string challengerId, IEnumerable<string> questionIds, DateTimeOffset createdAt)
    {
        Id = id;
        ChallengerId = challengerId;
        _questionIds = [.. questionIds];
        CreatedAt = createdAt;
    }

    public string Id { get; }
    public string ChallengerId { get; }
    public string? OpponentId { get; private set; }
    public IReadOnlyList<string> QuestionIds => _questionIds;
    public MatchState State { get; private set; } = MatchState.AwaitingOpponent;
    public LivePhase Phase { get; private set; } = LivePhase.Lobby;
    public DateTimeOffset? PhaseEndsAt { get; private set; }
    public IReadOnlyList<LiveRound> Rounds => _rounds;
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string? WinnerId { get; private set; }
    public bool IsDraw { get; private set; }

    /// <summary>The player who was recorded as having abandoned the duel, if it ended that way.</summary>
    public string? AbandonedBy { get; private set; }

    public bool IsOver => State is MatchState.Resolved or MatchState.Forfeited or MatchState.Abandoned or MatchState.NoContest;

    /// <summary>The round currently open for answers or being revealed, or null before the first one starts.</summary>
    public LiveRound? CurrentRound => _rounds.Count == 0 ? null : _rounds[^1];

    public static LiveMatch Create(string id, string challengerId, IReadOnlyList<string> questionIds, DateTimeOffset now)
    {
        if (!MatchRules.IsValidCount(questionIds.Count))
            throw new ArgumentException(
                $"A live duel needs one of {string.Join(", ", MatchRules.QuestionCountChoices)} questions, got {questionIds.Count}.", nameof(questionIds));
        if (questionIds.Distinct().Count() != questionIds.Count)
            throw new ArgumentException("A live duel cannot repeat a question.", nameof(questionIds));

        return new LiveMatch(id, challengerId, questionIds, now);
    }

    public bool IsParticipant(string playerId) => playerId == ChallengerId || playerId == OpponentId;

    public int Score(string playerId) => _rounds.Sum(r => r.Answers.TryGetValue(playerId, out var a) ? a.Score : 0);

    public int MissStreak(string playerId) => _missStreak.GetValueOrDefault(playerId);

    public void Join(string playerId, DateTimeOffset now)
    {
        if (playerId == ChallengerId) throw new InvalidOperationException("You cannot join your own challenge.");

        // Settle the clock first: a lobby nobody joined in time is already NoContest, and must not
        // be resurrected into a duel just because nobody had ticked it yet.
        Advance(now);
        if (State != MatchState.AwaitingOpponent) throw new InvalidOperationException("This challenge has already been taken.");

        OpponentId = playerId;
        State = MatchState.InProgress;
        Phase = LivePhase.Countdown;
        PhaseEndsAt = now + LiveRules.StartCountdown;
    }

    /// <summary>
    /// Moves the clock forward, crossing as many phase boundaries as <paramref name="now"/> has made
    /// due. Returns whether anything changed.
    /// </summary>
    public bool Advance(DateTimeOffset now)
    {
        if (IsOver) return false;

        // Checked once, against the real gap since the round we are actually sitting in was opened
        // — not the synthetic per-round gap the loop below uses, which is always exactly
        // MatchRules.QuestionTime and so could never look stale. A jump this size before anyone in
        // the live round has answered means the process was away, not that both players sat through
        // it in silence; that is settled here before any round is simulated.
        if (Phase == LivePhase.Question && CurrentRound is { Answers.Count: 0 } round
            && now - round.StartedAt > LiveRules.StaleAfter)
        {
            FinishNoContest(now);
            return true;
        }

        var changed = false;
        while (StepOnce(now)) changed = true;
        return changed;
    }

    public LiveAnswer Answer(string playerId, int slot, int choiceIndex, bool correct, DateTimeOffset now,
        Difficulty level = Difficulty.Medium)
    {
        RequireParticipant(playerId);

        // Advance is not called here: composing "settle the clock, then judge the answer" is the
        // grain's job (it calls Advance on every tick and before every answer). This method only
        // has to refuse an answer against a phase that has already moved on.
        if (Phase != LivePhase.Question || CurrentRound is not { } round)
            throw new InvalidOperationException("There is no question open to answer.");
        if (slot != round.Slot)
            throw new InvalidOperationException($"Expected an answer for round {round.Slot}, got {slot}.");
        if (round.HasAnswered(playerId))
            throw new InvalidOperationException("You have already answered this round.");
        if (choiceIndex < 0 || choiceIndex >= MatchRules.ChoicesPerQuestion)
            throw new InvalidOperationException($"Choice {choiceIndex} is out of range.");

        var taken = now - round.StartedAt;
        var answer = new LiveAnswer(choiceIndex, correct, Scoring.Score(correct, taken, MatchRules.QuestionTime, level), taken.TotalSeconds);
        round.Record(playerId, answer);
        _missStreak[playerId] = 0;

        if (round.Answers.Count >= 2)
        {
            Phase = LivePhase.Reveal;
            PhaseEndsAt = now + LiveRules.RevealTime;
        }

        return answer;
    }

    public LiveMatchSnapshot ToSnapshot() => new(
        Id, ChallengerId, OpponentId, [.. _questionIds], State, Phase, PhaseEndsAt,
        [.. _rounds.Select(r => new LiveRoundSnapshot(r.Slot, r.QuestionId, r.StartedAt, new Dictionary<string, LiveAnswer>(r.Answers)))],
        new Dictionary<string, int>(_missStreak), CreatedAt, EndedAt, WinnerId, IsDraw, AbandonedBy);

    public static LiveMatch FromSnapshot(LiveMatchSnapshot s)
    {
        var m = new LiveMatch(s.Id, s.ChallengerId, s.QuestionIds, s.CreatedAt)
        {
            OpponentId = s.OpponentId,
            State = s.State,
            Phase = s.Phase,
            PhaseEndsAt = s.PhaseEndsAt,
            EndedAt = s.EndedAt,
            WinnerId = s.WinnerId,
            IsDraw = s.IsDraw,
            AbandonedBy = s.AbandonedBy
        };
        foreach (var round in s.Rounds)
            m._rounds.Add(LiveRound.Restore(round.Slot, round.QuestionId, round.StartedAt, round.Answers));
        foreach (var (playerId, streak) in s.MissStreaks)
            m._missStreak[playerId] = streak;
        return m;
    }

    /// <summary>Advances at most one phase boundary. Returns whether it did.</summary>
    private bool StepOnce(DateTimeOffset now)
    {
        if (IsOver) return false;

        // The lobby has no PhaseEndsAt of its own — nobody has joined to start a countdown — so its
        // deadline is derived from CreatedAt instead.
        if (Phase == LivePhase.Lobby)
        {
            if (now < CreatedAt + LiveRules.LobbyExpires) return false;
            FinishNoContest(now);
            return true;
        }

        if (PhaseEndsAt is not { } endsAt) return false;

        // Every other deadline expires the instant now reaches it. A Question's close is the one
        // exception: MatchRules.NetworkGrace is tolerance on top of the deadline clients see, not
        // extra time on the schedule, so the round stays open through the grace window and only
        // closes once now is strictly past it — matching the boundary Scoring.Score already uses.
        if (Phase == LivePhase.Question)
        {
            if (now <= endsAt + MatchRules.NetworkGrace) return false;
        }
        else if (now < endsAt) return false;

        switch (Phase)
        {
            case LivePhase.Countdown:
                OpenRound(0, endsAt);
                return true;
            case LivePhase.Question:
                CloseRound(endsAt);
                return true;
            case LivePhase.Reveal:
                OpenNextRoundOrFinish(endsAt);
                return true;
            default:
                return false;
        }
    }

    private void OpenRound(int slot, DateTimeOffset at)
    {
        _rounds.Add(new LiveRound(slot, _questionIds[slot], at));
        Phase = LivePhase.Question;
        PhaseEndsAt = at + MatchRules.QuestionTime;
    }

    private void OpenNextRoundOrFinish(DateTimeOffset at)
    {
        var nextSlot = _rounds.Count;
        if (nextSlot >= _questionIds.Count)
        {
            FinishResolved(at);
            return;
        }

        OpenRound(nextSlot, at);
    }

    private void CloseRound(DateTimeOffset at)
    {
        var round = CurrentRound!;

        var abandoning = new List<string>();
        foreach (var playerId in Participants())
        {
            if (round.HasAnswered(playerId))
            {
                _missStreak[playerId] = 0;
                continue;
            }

            round.Record(playerId, new LiveAnswer(-1, false, 0, (at - round.StartedAt).TotalSeconds));
            var streak = _missStreak[playerId] = _missStreak.GetValueOrDefault(playerId) + 1;
            if (streak >= LiveRules.MissesBeforeAbandon) abandoning.Add(playerId);
        }

        if (abandoning.Count == 2)
        {
            FinishNoContest(at);
            return;
        }

        if (abandoning.Count == 1)
        {
            var quitter = abandoning[0];
            var winner = Participants().First(p => p != quitter);
            FinishAbandoned(quitter, winner, at);
            return;
        }

        Phase = LivePhase.Reveal;
        PhaseEndsAt = at + LiveRules.RevealTime;
    }

    private IEnumerable<string> Participants()
    {
        yield return ChallengerId;
        if (OpponentId is not null) yield return OpponentId;
    }

    private void FinishResolved(DateTimeOffset at)
    {
        State = MatchState.Resolved;
        Phase = LivePhase.Over;
        PhaseEndsAt = null;
        EndedAt = at;

        var mine = Score(ChallengerId);
        var theirs = OpponentId is null ? 0 : Score(OpponentId);
        IsDraw = mine == theirs;
        WinnerId = IsDraw ? null : (mine > theirs ? ChallengerId : OpponentId);
    }

    private void FinishAbandoned(string quitter, string winner, DateTimeOffset at)
    {
        State = MatchState.Abandoned;
        Phase = LivePhase.Over;
        PhaseEndsAt = null;
        EndedAt = at;
        AbandonedBy = quitter;
        WinnerId = winner;
        IsDraw = false;
    }

    private void FinishNoContest(DateTimeOffset at)
    {
        State = MatchState.NoContest;
        Phase = LivePhase.Over;
        PhaseEndsAt = null;
        EndedAt = at;
        WinnerId = null;
        IsDraw = false;
    }

    private void RequireParticipant(string playerId)
    {
        if (!IsParticipant(playerId)) throw new InvalidOperationException("You are not in this duel.");
    }
}
