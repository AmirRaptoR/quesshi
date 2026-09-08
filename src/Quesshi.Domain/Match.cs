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

    /// <summary>What the lobby's owner picked, and what the question set is drawn from at start.
    /// Privately settable rather than init-only: see <see cref="UpdateSettings"/>, the one place it
    /// ever changes after construction.</summary>
    public DuelSettings Settings { get; private set; }

    public Language Lang => Settings.Language;

    /// <summary>How many seats this lobby has, fixed at creation: 2 to 8.</summary>
    public int Capacity { get; }

    /// <summary>Every seated player, in join order. <c>Participants[0]</c> is always
    /// <see cref="OwnerId"/> — the one who created this lobby and the only one who may change its
    /// settings or start it.</summary>
    public IReadOnlyList<string> Participants => _participants;

    public string OwnerId => _participants[0];

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
    /// Supplies the question set <see cref="Settings"/> describes. In the product this happens once,
    /// when the lobby's owner starts the duel — wiring that call is a later step of issue #47, since
    /// it needs the question set builder the grain owns, not this class. Until it is called,
    /// <see cref="QuestionIds"/> stays empty and <see cref="ServeNext"/> refuses to serve anything
    /// (see its own remarks for why that matters).
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
        // unchanged: the owner-presses-Start affordance a bigger lobby needs (see Start) never gets a
        // chance to apply, because a two-seat lobby can never be in a state where it is needed — the
        // very join that seats the second player is always also the one that reaches Capacity.
        if (_participants.Count == Capacity) BeginDuel();
    }

    /// <summary>
    /// The actual state flip out of the lobby, shared by the two doors that reach it: <see cref="Join"/>
    /// reaching <see cref="Capacity"/> on its own, and the owner's explicit <see cref="Start"/>. Neither
    /// caller may invoke this before the question set is ready — the grain draws it (via
    /// <see cref="DrawQuestions"/>) one join early for the auto-start case, and <see cref="Start"/>
    /// checks <see cref="QuestionIds"/> itself before ever calling in here.
    /// </summary>
    private void BeginDuel() => State = MatchState.InProgress;

    /// <summary>
    /// The owner's explicit counterpart to <see cref="Join"/>'s auto-start: starts the duel once at
    /// least two are seated, for a lobby with room to spare that nobody is going to fill the rest of.
    /// Refused for anyone but the owner, below two participants, once the lobby has already left
    /// <see cref="MatchState.AwaitingOpponent"/>, or while <see cref="QuestionIds"/> is still empty —
    /// the grain draws the question set (via <see cref="DrawQuestions"/>) before ever calling this,
    /// since that needs the question bank it owns, not this class.
    /// </summary>
    public bool Start(string playerId, DateTimeOffset now)
    {
        if (State != MatchState.AwaitingOpponent || playerId != OwnerId || _participants.Count < 2 || _questionIds.Count == 0)
            return false;

        BeginDuel();
        return true;
    }

    /// <summary>
    /// A seated, non-owner player gives up their seat before the duel starts, freeing it for someone
    /// else to take. The owner cannot leave this way — there is no ownership transfer, so an owner's
    /// departure has to end the whole lobby instead (see <see cref="Cancel"/>). Settles the deadline
    /// first, mirroring <see cref="Join"/>. Returns false if nothing changed: the lobby has already
    /// left <see cref="MatchState.AwaitingOpponent"/>, the caller is the owner, or the caller was
    /// never seated.
    /// </summary>
    public bool Leave(string playerId, DateTimeOffset now)
    {
        TryForfeit(now);
        return State == MatchState.AwaitingOpponent && playerId != OwnerId && _participants.Remove(playerId);
    }

    /// <summary>
    /// The owner cancels their own lobby before it ever became a duel: <see cref="MatchState.NoContest"/>,
    /// with no ownership transfer to whoever else is seated — added here for the first time, since
    /// step 1 left <see cref="MatchState.NoContest"/> supported on this class but produced by nothing.
    /// Mirrors <see cref="LiveMatch"/>'s own owner-cancel path. Deliberately leaves <see cref="Standings"/>
    /// empty rather than calling <see cref="FinishWithStandings"/>: a cancelled lobby credits nobody,
    /// even a lone owner who had already served themself some questions before cancelling (see
    /// <c>MatchGrain.SettleAsync</c>'s own guard for why that matters). Settles the deadline first, so
    /// cancelling an already-expired lobby is a no-op rather than double-ending it. Refused for anyone
    /// but the owner, or once the lobby has already started or ended.
    /// </summary>
    public bool Cancel(string playerId, DateTimeOffset now)
    {
        TryForfeit(now);
        if (State != MatchState.AwaitingOpponent || playerId != OwnerId) return false;

        State = MatchState.NoContest;
        EndedAt = now;
        WinnerId = null;
        IsDraw = false;
        return true;
    }

    /// <summary>
    /// The owner changes what <see cref="DrawQuestions"/> will draw. "Settings are editable exactly
    /// while the question set is empty" is the one flag this checks — deliberately not a second,
    /// separate "locked" bit, so a legacy record (whose questions are always already drawn, see
    /// <see cref="FromSnapshot"/>) is correctly locked out of this too, with no extra state to keep in
    /// step. Refused for anyone but the owner.
    /// </summary>
    public bool UpdateSettings(string playerId, DuelSettings settings)
    {
        if (playerId != OwnerId || _questionIds.Count > 0) return false;

        Settings = settings;
        return true;
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

    /// <summary>
    /// Rebuilds a match from its persisted shape — either shape. Empty <see cref="MatchSnapshot.Participants"/>
    /// is the tell for a record written before that field existed: grain state in Redis is never cleared
    /// (see <see cref="MatchSnapshot"/>'s own remarks), so a blob this old is a real, ongoing possibility,
    /// not a hypothetical. Such a record is a lobby whose questions are already drawn — <see cref="ServeNext"/>
    /// and <see cref="SubmitAnswer"/> never required <c>InProgress</c>, so a challenger could finish an
    /// entire run while still <c>AwaitingOpponent</c> — and its settings can only be reconstructed, never
    /// recovered exactly: language and question count are read off the question set that already exists,
    /// while categories and levels were never recorded per-duel before <see cref="DuelSettings"/> existed,
    /// so they come back empty. That is not a loss for this record: <see cref="DuelSettings.CategoryIds"/>
    /// and <see cref="DuelSettings.Levels"/> are display-only and never used to draw, and a legacy match's
    /// questions are never drawn again — see <see cref="DrawQuestions"/>'s guard, which a legacy record
    /// satisfies for the same reason it can still join a joiner: <see cref="QuestionIds"/> is already full.
    ///
    /// This branch is permanent, not a step on the way to deleting it: the async history listing
    /// reactivates a finished match's grain to build its rows, so a blob written before this record
    /// existed can surface years from now exactly as it can today. Removing this branch would take an
    /// explicit, offline rewrite of every retained grain state in Redis -- there is no natural moment to
    /// run one and nothing to fall back on if it is wrong -- not the passage of time.
    /// </summary>
    public static Match FromSnapshot(MatchSnapshot s)
    {
        var legacy = s.Participants is not { Count: > 0 };
        var participants = legacy ? LegacyParticipants(s.ChallengerId!, s.OpponentId) : s.Participants;
        var settings = legacy ? new DuelSettings(s.Lang!.Value, s.QuestionIds.Count, [], []) : s.Settings;
        var capacity = legacy ? 2 : s.Capacity; // every pre-lobby match was exactly two seats

        var m = new Match(s.Id, s.Code, participants[0], settings, capacity, s.QuestionIds, s.CreatedAt)
        {
            State = s.State,
            EndedAt = s.EndedAt,
            WinnerId = s.WinnerId,
            IsDraw = s.IsDraw
        };
        m._participants.AddRange(participants.Skip(1));
        foreach (var (playerId, run) in s.Runs)
            m._runs[playerId] = PlayerRun.Restore(s.QuestionIds.Count, run.Answers, run.ServedAt);
        m._standings.AddRange(s.Standings ?? []);
        return m;
    }

    /// <summary>The two-player pair every match had before <see cref="Participants"/> existed, as the
    /// ordered list <see cref="Participants"/> replaced it with. A null opponent means nobody had
    /// joined yet — a one-seat list, not a phantom second participant.</summary>
    private static List<string> LegacyParticipants(string challengerId, string? opponentId) =>
        opponentId is null ? [challengerId] : [challengerId, opponentId];

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
