namespace Quesshi.Domain;

/// <summary>
/// A live duel: two to <see cref="Capacity"/> players on the same question at the same time, on a
/// shared clock. Pure state machine, like <see cref="Match"/> — no storage, no Orleans, no clock of
/// its own. Every method that needs "now" is handed it, and <see cref="Advance"/> is the only one
/// that reads it to decide what changed, so the timer and every answer can drive the same state
/// through the same door.
/// </summary>
public sealed class LiveMatch
{
    private readonly List<string> _participants;
    private readonly List<string> _questionIds;
    private readonly List<LiveRound> _rounds = [];
    private readonly Dictionary<string, int> _missStreak = [];
    private readonly List<Abandonment> _abandoners = [];
    private readonly List<Standing> _standings = [];

    private LiveMatch(string id, string code, string ownerId, DuelSettings settings, int capacity,
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

    /// <summary>The share code a friend types or follows — the same namespace an async match's code
    /// lives in, mirrored into <c>IMatchArchive</c> so it can be resolved by code at all.</summary>
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
    public LivePhase Phase { get; private set; } = LivePhase.Lobby;
    public DateTimeOffset? PhaseEndsAt { get; private set; }
    public IReadOnlyList<LiveRound> Rounds => _rounds;
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>The sole occupant of first place, or null when first place is shared. Reflects only
    /// the top of <see cref="Standings"/> — per-player outcomes must read <see cref="Standings"/>
    /// itself, never this scalar.</summary>
    public string? WinnerId { get; private set; }

    /// <summary>True when nobody won outright — first place is shared — not "everybody drew". A
    /// three-player duel with scores 100, 100, 50 is <c>IsDraw = true</c> for two draws and one
    /// loss, read from <see cref="Standings"/>, not three draws.</summary>
    public bool IsDraw { get; private set; }

    /// <summary>Every player who hit the miss-streak, in the order <c>CloseRound</c> found them, with
    /// the round slot they dropped in. Empty unless somebody abandoned.</summary>
    public IReadOnlyList<Abandonment> Abandoners => _abandoners;

    /// <summary>
    /// One <see cref="Standing"/> per participant once the duel is over — empty before then, and
    /// empty for a <see cref="MatchState.NoContest"/>, which credits nobody. Finishers rank by score
    /// above every abandoner regardless of score; see <see cref="Standing"/>'s own remarks for why.
    /// </summary>
    public IReadOnlyList<Standing> Standings => _standings;

    /// <summary>Why this ended a <see cref="MatchState.NoContest"/>, or null otherwise. Only
    /// <see cref="NoContestReason.AllAbandoned"/> is eligible for the abandonment penalty.</summary>
    public NoContestReason? Reason { get; private set; }

    public bool IsOver => State is MatchState.Resolved or MatchState.Forfeited or MatchState.Abandoned or MatchState.NoContest;

    /// <summary>The round currently open for answers or being revealed, or null before the first one starts.</summary>
    public LiveRound? CurrentRound => _rounds.Count == 0 ? null : _rounds[^1];

    /// <summary>
    /// The instant at which <see cref="Advance"/> would next change something, or null once
    /// <see cref="IsOver"/>. Mirrors the boundary <see cref="StepOnce"/> actually checks, so the
    /// clock driving this duel and the rules governing it never drift apart — and it is exactly the
    /// boundary the widened staleness test in <see cref="Advance"/> measures against, for the same
    /// reason.
    /// </summary>
    public DateTimeOffset? NextDueAt
    {
        get
        {
            if (IsOver) return null;
            return Phase switch
            {
                LivePhase.Lobby => CreatedAt + LiveRules.LobbyExpires,
                LivePhase.Question => PhaseEndsAt + MatchRules.NetworkGrace,
                _ => PhaseEndsAt
            };
        }
    }

    /// <summary>
    /// Opens a lobby for <paramref name="ownerId"/> with no question drawn yet — the set is drawn
    /// later, from whatever <paramref name="settings"/> says at that instant (see
    /// <see cref="DrawQuestions"/>), which is what lets a lobby's owner change settings for as long
    /// as nobody has started it.
    /// </summary>
    public static LiveMatch Create(string id, string code, string ownerId, DuelSettings settings, int capacity, DateTimeOffset now)
    {
        if (capacity is < 2 or > 8)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A duel lobby holds between 2 and 8 players.");

        return new LiveMatch(id, code, ownerId, settings, capacity, [], now);
    }

    /// <summary>
    /// Supplies the question set <see cref="Settings"/> describes. In the product this happens once,
    /// when the lobby's owner starts the duel — wiring that call is a later step of issue #47, since
    /// it needs the question set builder the grain owns, not this class. Until it is called,
    /// <see cref="QuestionIds"/> stays empty and a lobby cannot progress past
    /// <see cref="LivePhase.Countdown"/>.
    /// </summary>
    public void DrawQuestions(IReadOnlyList<string> questionIds)
    {
        if (Phase != LivePhase.Lobby) throw new InvalidOperationException("Questions can only be drawn before the duel starts.");
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
            throw new ArgumentException("A live duel cannot repeat a question.", nameof(questionIds));
    }

    public bool IsParticipant(string playerId) => _participants.Contains(playerId);

    public int Score(string playerId) => _rounds.Sum(r => r.Answers.TryGetValue(playerId, out var a) ? a.Score : 0);

    public int MissStreak(string playerId) => _missStreak.GetValueOrDefault(playerId);

    /// <summary>Everyone still playing — every participant minus whoever has abandoned. A round
    /// closes once every player in this set has answered it, and only these players are asked to
    /// answer at all; an abandoned player's turn is never waited on again.</summary>
    private IEnumerable<string> ActiveParticipants() => _participants.Where(p => !IsAbandoned(p));

    private bool IsAbandoned(string playerId) => _abandoners.Any(a => a.PlayerId == playerId);

    public void Join(string playerId, DateTimeOffset now)
    {
        if (_participants.Contains(playerId)) throw new InvalidOperationException("You are already in this duel.");

        // Settle the clock first: a lobby whose deadline has already passed is already NoContest,
        // and must not be resurrected into a duel just because nobody had ticked it yet.
        Advance(now);
        if (Phase != LivePhase.Lobby) throw new InvalidOperationException("This lobby is no longer open to join.");
        if (_participants.Count >= Capacity) throw new InvalidOperationException("This lobby is full.");

        _participants.Add(playerId);

        // No auto-start here, even for a two-seat lobby that has just filled its last seat: the
        // owner's Start is the only door into BeginDuel now (see Start's own remarks). A capacity-2
        // lobby used to be "not a special case of this rule — it is this rule, with Capacity == 2",
        // which is exactly what made an owner's Back press silently re-seat and start a finished
        // duel; removing the special case removes the bug.
    }

    /// <summary>
    /// The actual state flip out of the lobby. <see cref="Start"/> is now the only door that reaches
    /// this — a lobby reaching <see cref="Capacity"/> on its own no longer does (see <see cref="Join"/>'s
    /// own remarks) — so the question set is always ready by the time this runs: <see cref="Start"/>
    /// checks <see cref="QuestionIds"/> itself before ever calling in here.
    /// </summary>
    private void BeginDuel(DateTimeOffset now)
    {
        State = MatchState.InProgress;
        Phase = LivePhase.Countdown;
        PhaseEndsAt = now + LiveRules.StartCountdown;
    }

    /// <summary>
    /// The owner starts the duel once at least two are seated — the only way a lobby ever leaves
    /// <see cref="LivePhase.Lobby"/>, whether it is full or has room to spare. Refused for anyone but
    /// the owner, below two participants, once the lobby has already left <see cref="LivePhase.Lobby"/>,
    /// or while <see cref="QuestionIds"/> is still empty. The grain draws the question set (via
    /// <see cref="DrawQuestions"/>) before ever calling this, since that needs the question bank it
    /// owns, not this class.
    /// </summary>
    public bool Start(string playerId, DateTimeOffset now)
    {
        if (Phase != LivePhase.Lobby || playerId != OwnerId || _participants.Count < 2 || _questionIds.Count == 0)
            return false;

        BeginDuel(now);
        return true;
    }

    /// <summary>
    /// A seated, non-owner player gives up their seat before the duel starts, freeing it for someone
    /// else to take. The owner cannot leave this way — there is no ownership transfer, so an owner's
    /// departure has to end the whole lobby instead (the grain does this with <see cref="EndNoContest"/>
    /// and <see cref="NoContestReason.OwnerCancelled"/>, not through this method). Settles the clock
    /// first, mirroring <see cref="Join"/>. Returns false if nothing changed: the lobby has already
    /// left <see cref="LivePhase.Lobby"/>, the caller is the owner, or the caller was never seated.
    /// </summary>
    public bool Leave(string playerId, DateTimeOffset now)
    {
        Advance(now);
        return Phase == LivePhase.Lobby && playerId != OwnerId && _participants.Remove(playerId);
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

    /// <summary>
    /// The grain-facing counterpart to <see cref="Join"/>: never throws, and distinguishes why a
    /// join did not seat anyone, which <see cref="Join"/>'s single exception message does not. The
    /// same player joining twice is idempotent here rather than an error.
    /// </summary>
    public LiveJoinResult TryJoin(string playerId, DateTimeOffset now)
    {
        // The owner is Participants[0], not somebody standing outside their own lobby, so "can I
        // join?" asked by them is answered by the participant check below like anyone else's — they
        // are already in. This used to return SelfJoin ("you cannot join your own challenge"), which
        // was true in the old model where a challenger waited for an opponent instead of occupying a
        // seat, and became a real blocker once a lobby had a page: that page loads by joining, so an
        // owner opening the lobby they had just created was refused and told the invite did not exist.
        if (_participants.Contains(playerId))
        {
            Advance(now);
            return LiveJoinResult.AlreadyIn;
        }

        try
        {
            Join(playerId, now);
            return LiveJoinResult.Joined;
        }
        catch (InvalidOperationException)
        {
            // Join settles the clock before it throws, so State already reflects why this failed:
            // NoContest means the lobby's own clock ran out. Otherwise the lobby closed some other
            // way before this caller got a seat — and now there are two of those to tell apart: every
            // seat is actually occupied (Full), or the owner started early with room still unfilled
            // (Taken, the same value this always returned back when "closed" and "full" were the same
            // thing for every lobby). A capacity-2 lobby can only ever close by filling, so a stranger
            // arriving after it has always seen, and still sees, exactly one of these two — just Full
            // now instead of Taken, since that is what actually happened.
            if (State == MatchState.NoContest) return LiveJoinResult.Expired;
            return _participants.Count >= Capacity ? LiveJoinResult.Full : LiveJoinResult.Taken;
        }
    }

    /// <summary>
    /// Moves the clock forward, crossing as many phase boundaries as <paramref name="now"/> has made
    /// due. Returns whether anything changed.
    /// </summary>
    public bool Advance(DateTimeOffset now)
    {
        if (IsOver) return false;

        // Widened staleness test: whatever phase this duel is sitting in, if now is past that
        // phase's own due boundary by more than StaleAfter, the process was away for the whole gap
        // — not that everyone sat through it in silence. NextDueAt already names that boundary for
        // every phase but Lobby and is the same value StepOnce steps against below, so this guard
        // and the ordinary phase machinery can never disagree about where the line is.
        //
        // This deliberately covers more than the old guard did: an outage starting in Countdown or
        // Reveal, or in Question after somebody has already answered, used to slip past it entirely,
        // and the loop below would then simulate every remaining round with nobody answering — at
        // LiveRules.MissesBeforeAbandon rounds, that marks everyone abandoned. Today that lands on
        // NoContest either way, so the bug was invisible; with a reason code attached, simulating it
        // would turn a server outage into a real abandonment penalty. Ending it here instead,
        // before any round is simulated, is the conservative reading of a duel nobody was present
        // for: no stats, no penalty, no result — same as it already is for an expired lobby.
        //
        // Lobby keeps its own reason (LobbyExpired) rather than this one: a lobby passing its
        // deadline unattended is expiry, not absence, and StepOnce's own Lobby branch already
        // handles it below.
        if (Phase != LivePhase.Lobby && NextDueAt is { } dueAt && now - dueAt > LiveRules.StaleAfter)
        {
            FinishNoContest(now, NoContestReason.Stale);
            return true;
        }

        var changed = false;
        while (StepOnce(now)) changed = true;
        return changed;
    }

    /// <summary>
    /// Records one player's answer to the round in flight.
    /// <para>
    /// <paramref name="kind"/> and <paramref name="response"/> are what sorting and map questions
    /// added. The kind is <i>told</i> to this class rather than derived by it, exactly as
    /// <paramref name="correct"/> and <paramref name="level"/> already are: all three are facts about
    /// a <see cref="Question"/>, and this is a pure state machine with no question bank, no storage
    /// and no clock — handing it a <see cref="Question"/> to read them off would drag the whole
    /// question repository into the one class that deliberately depends on nothing. The grain knows
    /// the kind because it has just loaded the question to grade the answer, so passing it costs a
    /// parameter and buys the rule below.
    /// </para>
    /// <para>
    /// The kind is used for exactly one thing here — deciding whether the choice-range rule applies —
    /// which is why it is not stored on the answer: the question already knows what kind it is, and a
    /// second copy on every answer could only ever disagree with it.
    /// </para>
    /// </summary>
    public LiveAnswer Answer(string playerId, int slot, int choiceIndex, bool correct, DateTimeOffset now,
        Difficulty level = Difficulty.Medium, QuestionKind kind = QuestionKind.Choice, string? response = null)
    {
        // Settle the clock first: real time has passed whether or not this particular call turns
        // out to be valid, so a rejected answer still leaves behind whatever this advanced — the
        // caller must persist that even when the rest of this method throws. Because a Question's
        // close already waits out MatchRules.NetworkGrace (see StepOnce), this never closes a round
        // out from under an answer that arrives inside the grace window; it only catches one that
        // arrives after it.
        Advance(now);
        RequireParticipant(playerId);
        if (IsAbandoned(playerId)) throw new InvalidOperationException("You have abandoned this duel and can no longer answer.");

        if (Phase != LivePhase.Question || CurrentRound is not { } round)
            throw new InvalidOperationException("There is no question open to answer.");
        if (slot != round.Slot)
            throw new InvalidOperationException($"Expected an answer for round {round.Slot}, got {slot}.");
        if (round.HasAnswered(playerId))
            throw new InvalidOperationException("You have already answered this round.");

        // The range check belongs to Choice alone. A sorting or map answer arrives with ChoiceIndex
        // at -1 — the same sentinel a timeout uses — because there is no choice to point at, and the
        // unconditional version of this rule would reject every one of them: the two new kinds would
        // be unanswerable in a live duel while looking, from the outside, merely late. Their own
        // validation happened before this call, in the grain: a sorting order that is not a
        // permutation and a map answer that does not parse are refused there and never reach here,
        // which is also where the -1 comes from, so nothing else in this method has to guess.
        if (kind == QuestionKind.Choice && (choiceIndex < 0 || choiceIndex >= MatchRules.ChoicesPerQuestion))
            throw new InvalidOperationException($"Choice {choiceIndex} is out of range.");

        var taken = now - round.StartedAt;
        var answer = new LiveAnswer(choiceIndex, correct, Scoring.Score(correct, taken, MatchRules.QuestionTime, level), taken.TotalSeconds,
            response);
        round.Record(playerId, answer);
        _missStreak[playerId] = 0;

        // A round closes once every player still playing has answered it — not every participant
        // ever seated. Counting an abandoned player's silence forever would mean the survivors of a
        // dropped third sat through the full timeout on every remaining round, since that answer can
        // now never arrive.
        if (round.Answers.Count >= ActiveParticipants().Count())
        {
            Phase = LivePhase.Reveal;
            PhaseEndsAt = now + LiveRules.RevealTime;
        }

        return answer;
    }

    /// <summary>
    /// The admin/operational kill path: ends an in-flight duel early with no winner. Every call site
    /// today is either the owner walking away from a lobby that never started, or an operational
    /// failure mid-duel (a question nothing can resolve); neither is the players' doing, so this
    /// defaults to a reason that is never penalty-eligible.
    /// <see cref="NoContestReason.AllAbandoned"/> is earned only by three real misses each, in
    /// <see cref="CloseRound"/> — never handed out by a caller.
    /// </summary>
    public void EndNoContest(DateTimeOffset now, NoContestReason reason = NoContestReason.LobbyExpired)
    {
        if (IsOver) return;
        FinishNoContest(now, reason);
    }

    public LiveMatchSnapshot ToSnapshot() => new(
        Id, Code, [.. _participants], Capacity, Settings, [.. _questionIds], State, Phase, PhaseEndsAt,
        [.. _rounds.Select(r => new LiveRoundSnapshot(r.Slot, r.QuestionId, r.StartedAt, new Dictionary<string, LiveAnswer>(r.Answers)))],
        new Dictionary<string, int>(_missStreak), CreatedAt, EndedAt, WinnerId, IsDraw, [.. _abandoners], [.. _standings], Reason);

    /// <summary>
    /// Rebuilds a live duel from its persisted shape — either shape. See <see cref="LiveMatchSnapshot"/>'s
    /// own remarks for why a pre-migration blob is a real possibility rather than a hypothetical, and
    /// <see cref="Match.FromSnapshot"/>'s remarks for why a legacy record's settings can only be
    /// reconstructed (language and count from the drawn question set; categories and levels empty,
    /// since they are display-only and were never recorded per-duel before now). The one thing genuinely
    /// live-specific here is <see cref="Abandoners"/>: a legacy record carries at most a single quitter's
    /// id in <see cref="LiveMatchSnapshot.AbandonedBy"/>, because the two-player shape could never produce
    /// more than one, and with only one abandoner there is nothing to rank it against — the round slot it
    /// dropped in genuinely does not matter, unlike for an N-player record where it decides ties.
    ///
    /// Like its async counterpart, this branch is permanent rather than a step towards deleting it:
    /// nothing calls ClearStateAsync, and the async history listing reactivates a finished duel's grain
    /// to build its rows, so a blob this old remains a live possibility indefinitely. Removing it would
    /// take an explicit, offline rewrite of every retained grain state in Redis, not the mere passage
    /// of time.
    /// </summary>
    public static LiveMatch FromSnapshot(LiveMatchSnapshot s)
    {
        var legacy = s.Participants is not { Count: > 0 };
        var participants = legacy ? LegacyParticipants(s.ChallengerId!, s.OpponentId) : s.Participants;
        var settings = legacy ? new DuelSettings(s.Lang!.Value, s.QuestionIds.Count, [], []) : s.Settings;
        var capacity = legacy ? 2 : s.Capacity; // every pre-lobby duel was exactly two seats

        var m = new LiveMatch(s.Id, s.Code, participants[0], settings, capacity, s.QuestionIds, s.CreatedAt)
        {
            State = s.State,
            Phase = s.Phase,
            PhaseEndsAt = s.PhaseEndsAt,
            EndedAt = s.EndedAt,
            WinnerId = s.WinnerId,
            IsDraw = s.IsDraw,
            Reason = s.Reason
        };
        m._participants.AddRange(participants.Skip(1));
        foreach (var round in s.Rounds)
            m._rounds.Add(LiveRound.Restore(round.Slot, round.QuestionId, round.StartedAt, round.Answers));
        foreach (var (playerId, streak) in s.MissStreaks)
            m._missStreak[playerId] = streak;

        if (legacy)
        {
            if (s.AbandonedBy is { } abandonedBy) m._abandoners.Add(new Abandonment(abandonedBy, RoundSlot: 0));
        }
        else
        {
            m._abandoners.AddRange(s.Abandoners);
        }

        m._standings.AddRange(s.Standings ?? []);
        return m;
    }

    /// <summary>The two-player pair every live duel had before <see cref="Participants"/> existed, as
    /// the ordered list <see cref="Participants"/> replaced it with. A null opponent means nobody had
    /// joined yet — a one-seat list, not a phantom second participant.</summary>
    private static List<string> LegacyParticipants(string challengerId, string? opponentId) =>
        opponentId is null ? [challengerId] : [challengerId, opponentId];

    /// <summary>Advances at most one phase boundary. Returns whether it did.</summary>
    private bool StepOnce(DateTimeOffset now)
    {
        if (IsOver) return false;

        // The lobby has no PhaseEndsAt of its own — nobody has joined to start a countdown — so its
        // deadline is derived from CreatedAt instead.
        if (Phase == LivePhase.Lobby)
        {
            if (now < CreatedAt + LiveRules.LobbyExpires) return false;
            FinishNoContest(now, NoContestReason.LobbyExpired);
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
            FinishWithStandings(MatchState.Resolved, at);
            return;
        }

        OpenRound(nextSlot, at);
    }

    private void CloseRound(DateTimeOffset at)
    {
        var round = CurrentRound!;
        var activeBefore = ActiveParticipants().ToList();
        var justAbandoned = new List<string>();

        foreach (var playerId in activeBefore)
        {
            if (round.HasAnswered(playerId))
            {
                _missStreak[playerId] = 0;
                continue;
            }

            round.Record(playerId, new LiveAnswer(-1, false, 0, (at - round.StartedAt).TotalSeconds));
            var streak = _missStreak[playerId] = _missStreak.GetValueOrDefault(playerId) + 1;
            if (streak >= LiveRules.MissesBeforeAbandon) justAbandoned.Add(playerId);
        }

        // Recorded here, not before the loop above: the round slot a player abandons in is exactly
        // the CloseRound call that just gave them their third consecutive miss, so appending inline
        // gets that for free instead of threading it through separately.
        foreach (var playerId in justAbandoned) _abandoners.Add(new Abandonment(playerId, round.Slot));

        var remaining = activeBefore.Count - justAbandoned.Count;

        // Whoever was still active before this round can only ever have been >= 2: the moment it
        // ever dropped to 1 or 0, one of the two branches below already ended the duel, so this
        // round is never reached with fewer than two players still in it.
        if (remaining == 0)
        {
            FinishNoContest(at, NoContestReason.AllAbandoned);
            return;
        }

        if (remaining == 1)
        {
            // Exactly one player is still standing: they win by abandonment, whatever the scoreboard
            // says — an abandoner ranks below every finisher regardless of score (see Standing's own
            // remarks), which BuildStandings applies here identically to FinishResolved.
            FinishWithStandings(MatchState.Abandoned, at);
            return;
        }

        Phase = LivePhase.Reveal;
        PhaseEndsAt = at + LiveRules.RevealTime;
    }

    private void FinishWithStandings(MatchState state, DateTimeOffset at)
    {
        State = state;
        Phase = LivePhase.Over;
        PhaseEndsAt = null;
        EndedAt = at;

        _standings.Clear();
        _standings.AddRange(BuildStandings());

        var first = _standings.Where(s => s.Place == 1).ToList();
        WinnerId = first.Count == 1 ? first[0].PlayerId : null;
        IsDraw = first.Count > 1;
    }

    /// <summary>
    /// Ranks every participant for a duel ending with real results: every non-abandoner above every
    /// abandoner regardless of score, and within each group by whatever actually decided it — score
    /// for a finisher, how long they lasted (round slot, descending) for an abandoner. Ties inside a
    /// group share a place; competition ranking means the group after a tie skips ahead by however
    /// many shared it, so "two tied for first, one below" reads as places 1, 1, 3, not 1, 1, 2.
    /// </summary>
    private List<Standing> BuildStandings()
    {
        var abandonedSlot = _abandoners.ToDictionary(a => a.PlayerId, a => a.RoundSlot);

        var ranked = _participants
            .Select(id => (PlayerId: id, IsAbandoner: abandonedSlot.ContainsKey(id), Score: Score(id), Slot: abandonedSlot.GetValueOrDefault(id)))
            .OrderBy(p => p.IsAbandoner)
            .ThenByDescending(p => p.IsAbandoner ? 0 : p.Score)
            .ThenByDescending(p => p.IsAbandoner ? p.Slot : 0)
            .ToList();

        var places = new int[ranked.Count];
        for (var i = 0; i < ranked.Count; i++)
        {
            var tiedWithPrevious = i > 0
                && ranked[i].IsAbandoner == ranked[i - 1].IsAbandoner
                && (ranked[i].IsAbandoner ? ranked[i].Slot == ranked[i - 1].Slot : ranked[i].Score == ranked[i - 1].Score);
            places[i] = tiedWithPrevious ? places[i - 1] : i + 1;
        }

        // Outcome depends on the final shape of first place, which is only known once every place is
        // assigned: its sole occupant wins, several sharing it each draw, and everyone else — whether
        // they finished lower or abandoned — loses.
        var firstPlaceCount = places.Count(p => p == 1);

        return [.. ranked.Select((p, i) => new Standing(
            p.PlayerId,
            p.IsAbandoner ? 0 : p.Score,
            places[i],
            places[i] != 1 ? MatchOutcome.Loss : firstPlaceCount == 1 ? MatchOutcome.Win : MatchOutcome.Draw))];
    }

    private void FinishNoContest(DateTimeOffset at, NoContestReason reason)
    {
        State = MatchState.NoContest;
        Phase = LivePhase.Over;
        PhaseEndsAt = null;
        EndedAt = at;
        WinnerId = null;
        IsDraw = false;
        Reason = reason;
    }

    private void RequireParticipant(string playerId)
    {
        if (!IsParticipant(playerId)) throw new InvalidOperationException("You are not in this duel.");
    }
}
