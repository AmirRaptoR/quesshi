namespace Quesshi.Domain;

/// <summary>
/// An asynchronous duel: two to <see cref="Capacity"/> players who each play the same questions on
/// their own schedule — a run finishes whenever the player finishes it, not on a shared clock. Pure
/// state machine, like <see cref="LiveMatch"/> — no storage, no Orleans, no clock of its own. Every
/// method that needs "now" is handed it, which is what makes the rules testable.
/// </summary>
public sealed class Match
{
    private readonly List<string> _participants;
    private readonly List<string> _questionIds;
    private readonly Dictionary<string, PlayerRun> _runs = [];
    private readonly List<Standing> _standings = [];

    private Match(string id, string code, string ownerId, DuelSettings settings, int capacity,
        IEnumerable<string> questionIds, DateTimeOffset createdAt)
    {
        Id = id;
        Code = code;
        Settings = settings;
        Capacity = capacity;
        _participants = [ownerId];
        _questionIds = [.. questionIds];
        CreatedAt = createdAt;
    }

    public string Id { get; }
    public string Code { get; }

    /// <summary>What the lobby's owner picked, and what the question set is drawn from at start.</summary>
    public DuelSettings Settings { get; }

    public Language Lang => Settings.Language;

    /// <summary>How many seats this lobby has, fixed at creation: 2 to 8.</summary>
    public int Capacity { get; }

    /// <summary>Every seated player, in join order. <c>Participants[0]</c> is always
    /// <see cref="OwnerId"/> — the one who created this lobby and the only one who may change its
    /// settings or start it.</summary>
    public IReadOnlyList<string> Participants => _participants;

    public string OwnerId => _participants[0];

    /// <summary>
    /// Compatibility accessor for the two-player shape <see cref="Participants"/> replaces. Every
    /// consumer of it migrates to <see cref="Participants"/>/<see cref="OwnerId"/> across the
    /// following steps of issue #47; issue #56 deletes this once none is left.
    /// </summary>
    [Obsolete("Use OwnerId (or Participants[0]). Deleted in issue #56.")]
    public string ChallengerId => OwnerId;

    /// <summary>
    /// Compatibility accessor: the second seat, or null if it is not yet taken. Meaningless once a
    /// lobby holds more than two, which is exactly why it is obsolete rather than generalised.
    /// Deleted in issue #56.
    /// </summary>
    [Obsolete("Use Participants. Deleted in issue #56.")]
    public string? OpponentId => _participants.Count > 1 ? _participants[1] : null;

    public IReadOnlyList<string> QuestionIds => _questionIds;
    public MatchState State { get; private set; } = MatchState.AwaitingOpponent;
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>The sole occupant of first place, or null when first place is shared. Reflects only
    /// the top of <see cref="Standings"/> — per-player outcomes must read <see cref="Standings"/>
    /// itself, never this scalar.</summary>
    public string? WinnerId { get; private set; }

    /// <summary>True when nobody won outright — first place is shared — not "everybody drew". A
    /// three-player duel with banked scores 100, 100, 50 is <c>IsDraw = true</c> for two draws and
    /// one loss, read from <see cref="Standings"/>, not three draws.</summary>
    public bool IsDraw { get; private set; }

    /// <summary>
    /// One <see cref="Standing"/> per participant once the duel is over — empty before then. Unlike
    /// <see cref="LiveMatch.Standings"/> there is no abandoners group: async has no per-round
    /// abandonment, so every participant is ranked purely by what they banked, whether their run
    /// ever finished or not (see <see cref="TryForfeit"/>'s remarks).
    /// </summary>
    public IReadOnlyList<Standing> Standings => _standings;

    /// <summary>
    /// <c>NoContest</c> joins the terminal states here: an async lobby the owner cancels before it
    /// ever starts must read as over, exactly as a live one does, even though nothing in this class
    /// yet produces that state on its own (owner-cancellation is grain-layer work, a later step of
    /// issue #47) — a snapshot can already carry it, and this must not misreport it as still running.
    /// </summary>
    public bool IsOver => State is MatchState.Resolved or MatchState.Forfeited or MatchState.NoContest;

    /// <summary>
    /// Opens a lobby for <paramref name="ownerId"/> with no question drawn yet — the set is drawn
    /// later, from whatever <paramref name="settings"/> says at that instant (see
    /// <see cref="DrawQuestions"/>), which is what lets a lobby's owner change settings for as long
    /// as nobody has started it.
    /// </summary>
    public static Match Create(string id, string code, string ownerId, DuelSettings settings, int capacity, DateTimeOffset now)
    {
        if (capacity is < 2 or > 8)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A duel lobby holds between 2 and 8 players.");

        return new Match(id, code, ownerId, settings, capacity, [], now);
    }

    /// <summary>
    /// The pre-lobby shape every existing caller still builds a duel through: a capacity-2 lobby
    /// whose question set is already drawn, exactly as every async duel was built before settings and
    /// N players existed. <c>MatchGrain.CreateAsync</c> is migrated to the settings-aware
    /// <see cref="Create(string, string, string, DuelSettings, int, DateTimeOffset)"/> plus
    /// <see cref="DrawQuestions"/> in a later step of issue #47; issue #56 deletes this overload.
    /// </summary>
    [Obsolete("Build a DuelSettings and call the capacity-aware Create, then DrawQuestions. Deleted in issue #56.")]
    public static Match Create(string id, string code, Language lang, string challengerId, IReadOnlyList<string> questionIds, DateTimeOffset now)
    {
        var settings = DuelSettings.Create(lang, questionIds.Count, [], []);
        ValidateQuestionIds(questionIds);
        return new Match(id, code, challengerId, settings, capacity: 2, questionIds, now);
    }

    /// <summary>
    /// Supplies the question set <see cref="Settings"/> describes. In the product this happens once,
    /// when the lobby's owner starts the duel — wiring that call is a later step of issue #47, since
    /// it needs the question set builder the grain owns, not this class. Until it is called,
    /// <see cref="QuestionIds"/> stays empty and <see cref="ServeNext"/> refuses to serve anything
    /// (see its own remarks for why that matters). The pre-lobby
    /// <see cref="Create(string, string, Language, string, IReadOnlyList{string}, DateTimeOffset)"/>
    /// overload above never needs this: it draws its set at construction, the way every duel did
    /// before lobbies existed.
    /// </summary>
    public void DrawQuestions(IReadOnlyList<string> questionIds)
    {
        if (State != MatchState.AwaitingOpponent) throw new InvalidOperationException("Questions can only be drawn before the duel starts.");
        if (_questionIds.Count > 0) throw new InvalidOperationException("This duel's questions are already drawn.");
        if (questionIds.Count != Settings.QuestionCount)
            throw new ArgumentException(
                $"This duel's settings call for {Settings.QuestionCount} questions, got {questionIds.Count}.", nameof(questionIds));
        ValidateQuestionIds(questionIds);

        _questionIds.AddRange(questionIds);
    }

    private static void ValidateQuestionIds(IReadOnlyList<string> questionIds)
    {
        if (questionIds.Distinct().Count() != questionIds.Count)
            throw new ArgumentException("A match cannot repeat a question.", nameof(questionIds));
    }

    public void Join(string playerId, DateTimeOffset now)
    {
        if (_participants.Contains(playerId)) throw new InvalidOperationException("You are already in this duel.");

        // Settle the clock first: a lobby whose 48-hour deadline has already passed is already
        // Forfeited, and must not be resurrected into a duel just because nobody's reminder had
        // ticked it yet — the bug this closes is exactly that the grain's forfeit reminder was the
        // *only* thing enforcing the deadline. Mirrors LiveMatch.Join calling Advance(now) before
        // checking Phase.
        TryForfeit(now);
        if (State != MatchState.AwaitingOpponent) throw new InvalidOperationException("This challenge has already been taken.");

        _participants.Add(playerId);

        // A capacity-2 lobby starting the instant its second seat fills is not a special case of
        // this rule — it is this rule, with Capacity == 2. That is exactly what keeps 1v1 behaviour
        // unchanged: the owner-presses-Start affordance a bigger lobby needs is a later step's
        // concern (see DrawQuestions' remarks), and a two-seat lobby can never be in a state where
        // it applies.
        if (_participants.Count == Capacity)
            State = MatchState.InProgress;
    }

    public bool IsParticipant(string playerId) => _participants.Contains(playerId);

    public PlayerRun? RunOf(string playerId) => _runs.GetValueOrDefault(playerId);

    /// <summary>You may see another player's run only once your own is done. While the match is still
    /// running this is the only rule; once it is over everyone sees everything regardless (see
    /// <see cref="TryForfeit"/>'s remarks) — that reveal is applied by the caller, exactly as
    /// <c>MatchGrain.View</c> already does with <c>CanReveal(forPlayerId) || IsOver</c>.</summary>
    public bool CanReveal(string playerId) => RunOf(playerId)?.Finished == true;

    public ServedQuestion ServeNext(string playerId, DateTimeOffset now)
    {
        RequireParticipant(playerId);
        if (IsOver) throw new InvalidOperationException("This match is over.");

        // Nothing may be served before the question set is drawn: ServeNext below creates a
        // PlayerRun on demand, and PlayerRun with a Total of zero is Finished the instant it exists
        // (PlayerRun.cs). Without this guard, an owner sitting alone in a fresh lobby could serve
        // themselves a run that is permanently, silently "done" before a single question is drawn.
        if (_questionIds.Count == 0) throw new InvalidOperationException("This duel's questions have not been drawn yet.");

        var run = _runs.TryGetValue(playerId, out var existing) ? existing : _runs[playerId] = new PlayerRun(_questionIds.Count);
        if (run.Finished) throw new InvalidOperationException("You have already finished your run.");

        run.MarkServed(now);
        return new ServedQuestion(run.NextSlot, _questionIds[run.NextSlot], now);
    }

    public AnswerRecord SubmitAnswer(string playerId, int slot, int choiceIndex, bool correct, DateTimeOffset now,
        Difficulty level = Difficulty.Medium)
    {
        RequireParticipant(playerId);

        // A terminal match must refuse an answer outright, not merely leave it unable to change the
        // outcome. Without this, a question served just before a forfeit can still be answered after
        // it: the run mutates, TryResolve below re-runs, and the caller's own "was this already over"
        // check — taken before this call — reads true, so nothing ever settles the changed result.
        if (IsOver) throw new InvalidOperationException("This match is over.");

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

    /// <summary>
    /// Ends a match that has sat untouched past the deadline. Returns false if it was not due.
    /// Standings cannot wait for "the last run to finish" here — forfeiture is precisely the case
    /// where some run never will — so they are built from whatever each participant had banked at
    /// this instant. An unfinished run contributes its partial score and nothing else: it is not
    /// "still playing" any more, because the match forfeiting means it never will finish, but it
    /// still counts for whatever it managed. This mirrors the rule <c>MatchGrain.View</c> already
    /// applies for reveal — <c>CanReveal(forPlayerId) || IsOver</c> — carried into standings: once
    /// the match is over, results are for everyone, finished or not.
    /// </summary>
    public bool TryForfeit(DateTimeOffset now)
    {
        if (IsOver) return false;
        if (now < CreatedAt + MatchRules.ForfeitAfter) return false;

        FinishWithStandings(MatchState.Forfeited, now);
        return true;
    }

    public MatchSnapshot ToSnapshot() => new(
        Id, Code, [.. _participants], Capacity, Settings, [.. _questionIds], State, CreatedAt, EndedAt, WinnerId, IsDraw,
        _runs.ToDictionary(kv => kv.Key, kv => new RunSnapshot([.. kv.Value.Answers], kv.Value.ServedAt)),
        [.. _standings]);

    public static Match FromSnapshot(MatchSnapshot s)
    {
        var m = new Match(s.Id, s.Code, s.Participants[0], s.Settings, s.Capacity, s.QuestionIds, s.CreatedAt)
        {
            State = s.State,
            EndedAt = s.EndedAt,
            WinnerId = s.WinnerId,
            IsDraw = s.IsDraw
        };
        m._participants.AddRange(s.Participants.Skip(1));
        foreach (var (playerId, run) in s.Runs)
            m._runs[playerId] = PlayerRun.Restore(s.QuestionIds.Count, run.Answers, run.ServedAt);
        m._standings.AddRange(s.Standings);
        return m;
    }

    /// <summary>
    /// Resolves once every seated participant has finished their run — never earlier, and never for
    /// a lobby that has not actually started (<see cref="State"/> still <c>AwaitingOpponent</c>,
    /// which is also true of a lone owner playing before anyone joins: see
    /// <c>MatchTests.The_challenger_can_play_before_anyone_joins</c>). A lobby with room for more
    /// than has joined so far therefore never resolves early just because everyone currently seated
    /// happens to be done — it waits for <see cref="TryForfeit"/>'s deadline instead, exactly as an
    /// unfilled seat does today.
    /// </summary>
    private void TryResolve(DateTimeOffset now)
    {
        if (State != MatchState.InProgress) return;
        if (_participants.Any(p => RunOf(p)?.Finished != true)) return;

        FinishWithStandings(MatchState.Resolved, now);
    }

    private void FinishWithStandings(MatchState state, DateTimeOffset now)
    {
        State = state;
        EndedAt = now;

        _standings.Clear();
        _standings.AddRange(BuildStandings());

        var first = _standings.Where(s => s.Place == 1).ToList();
        WinnerId = first.Count == 1 ? first[0].PlayerId : null;
        IsDraw = first.Count > 1;
    }

    /// <summary>
    /// Ranks every participant by whatever they banked — score alone, since async has no
    /// abandonment to rank beneath it (contrast <see cref="LiveMatch"/>'s equivalent, which groups
    /// abandoners below every finisher first). Ties share a place, and the place after a tie skips
    /// ahead by however many shared it (competition ranking): two tied for first and one below read
    /// as places 1, 1, 3, not 1, 1, 2.
    /// </summary>
    private List<Standing> BuildStandings()
    {
        var ranked = _participants
            .Select(id => (PlayerId: id, Score: RunOf(id)?.Score ?? 0))
            .OrderByDescending(p => p.Score)
            .ToList();

        var places = new int[ranked.Count];
        for (var i = 0; i < ranked.Count; i++)
            places[i] = i > 0 && ranked[i].Score == ranked[i - 1].Score ? places[i - 1] : i + 1;

        var firstPlaceCount = places.Count(p => p == 1);

        return [.. ranked.Select((p, i) => new Standing(
            p.PlayerId,
            p.Score,
            places[i],
            places[i] != 1 ? MatchOutcome.Loss : firstPlaceCount == 1 ? MatchOutcome.Win : MatchOutcome.Draw))];
    }

    private void RequireParticipant(string playerId)
    {
        if (!IsParticipant(playerId)) throw new InvalidOperationException("You are not in this match.");
    }
}
