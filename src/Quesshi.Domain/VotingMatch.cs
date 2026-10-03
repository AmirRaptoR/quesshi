namespace Quesshi.Domain;

/// <summary>
/// The persistent state machine for a voting match. Voting is deliberately separate from
/// <see cref="Match"/> and <see cref="LiveMatch"/>: there is no question timer, score, grading,
/// presence or winner. A slot closes only when every active participant has submitted an answer.
/// </summary>
public sealed class VotingMatch
{
    private readonly List<string> _participants;
    private readonly List<VotingQuestion> _questions;
    private readonly List<VotingSlot> _slots = [];
    private readonly HashSet<string> _inactive = [];
    private int? _currentSlot;

    private VotingMatch(string id, string code, string ownerId, int capacity,
        IEnumerable<VotingQuestion> questions, DateTimeOffset createdAt)
    {
        Id = id;
        Code = code;
        Capacity = capacity;
        _participants = [ownerId];
        _questions = [.. questions];
        CreatedAt = createdAt;
    }

    public string Id { get; }
    public string Code { get; }
    public int Capacity { get; }
    public IReadOnlyList<string> Participants => _participants;
    public string OwnerId => _participants[0];
    public MatchState State { get; private set; } = MatchState.AwaitingOpponent;
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public bool IsOver => State is MatchState.Resolved or MatchState.NoContest;

    /// <summary>All slots that have ever been served, including slots whose barriers are closed.</summary>
    public IReadOnlyList<VotingSlot> Slots => _slots;

    /// <summary>The slot currently accepting answers, or null before start and after completion.</summary>
    public VotingSlot? CurrentSlot =>
        State == MatchState.InProgress && _currentSlot is { } index ? _slots[index] : null;

    public int? CurrentSlotIndex => CurrentSlot?.Slot;

    /// <summary>The currently active roster. It is fixed at start; after start, leaving only marks a
    /// participant inactive and never removes them from the original option roster.</summary>
    public IReadOnlyList<string> ActiveParticipants => _participants.Where(IsActive).ToArray();

    public IReadOnlyList<string> InactiveParticipants => _participants.Where(p => !IsActive(p)).ToArray();

    public static VotingMatch Create(string id, string code, string ownerId, int capacity,
        IReadOnlyList<VotingQuestion> questions, DateTimeOffset now)
    {
        if (capacity is < 2 or > VotingRules.MaxParticipants)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity,
                "A voting lobby holds between 2 and 8 players.");
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0)
            throw new ArgumentException("A voting match needs at least one question.", nameof(questions));
        if (questions.Any(q => q is null))
            throw new ArgumentException("A voting match cannot contain a null question.", nameof(questions));

        return new VotingMatch(id, code, ownerId, capacity, questions, now);
    }

    /// <summary>Compatibility overload for callers that naturally put the question set before the seats.</summary>
    public static VotingMatch Create(string id, string code, string ownerId,
        IReadOnlyList<VotingQuestion> questions, int capacity, DateTimeOffset now) =>
        Create(id, code, ownerId, capacity, questions, now);

    /// <summary>Seats a participant while the lobby is open. Starting fixes the roster permanently.</summary>
    public bool Join(string playerId, DateTimeOffset now)
    {
        Advance(now);
        if (State != MatchState.AwaitingOpponent || _participants.Contains(playerId)
            || _participants.Count >= Capacity)
            return false;

        _participants.Add(playerId);
        return true;
    }

    public bool Join(string playerId) => Join(playerId, CreatedAt);

    /// <summary>
    /// Starts and serves slot zero. Only the owner can start, and at least two participants must be
    /// seated. The question objects have already been resolved by the caller, so serving cannot
    /// produce a half-built slot.
    /// </summary>
    public bool Start(string playerId, DateTimeOffset now)
    {
        if (State != MatchState.AwaitingOpponent || playerId != OwnerId || _participants.Count < 2)
            return false;

        State = MatchState.InProgress;
        ServeNext(now);
        return true;
    }

    /// <summary>Removes a non-owner from an open lobby, or marks any participant inactive in progress.</summary>
    public bool Leave(string playerId, DateTimeOffset now)
    {
        Advance(now);
        if (State == MatchState.AwaitingOpponent)
        {
            if (playerId == OwnerId) return Cancel(playerId, now);
            return _participants.Remove(playerId);
        }

        if (State != MatchState.InProgress || !IsActive(playerId)) return false;
        _inactive.Add(playerId);
        if (ActiveParticipants.Count < 2) Finish(MatchState.NoContest, now);
        else TryAdvance(now);
        return true;
    }

    /// <summary>Owner cancellation before start. Voting cancellation never creates a penalty.</summary>
    public bool Cancel(string playerId, DateTimeOffset now)
    {
        if (State != MatchState.AwaitingOpponent || playerId != OwnerId) return false;
        Finish(MatchState.NoContest, now);
        return true;
    }

    /// <summary>
    /// Records or replaces the caller's answer for the current slot. <paramref name="now"/> is the
    /// trusted aggregate clock and is the only timestamp used for expiry, transitions and storage.
    /// The timestamp supplied on the input answer is treated as untrusted transport data. Once a
    /// barrier closes, the old slot is no longer current and every later submission for it is
    /// rejected.
    /// </summary>
    public VotingAnswer Answer(string playerId, int slot, VotingAnswer answer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(answer);
        Advance(now);
        RequireActiveParticipant(playerId);

        var current = CurrentSlot;
        if (current is null || current.Slot != slot)
            throw new InvalidOperationException("There is no voting slot open for that answer.");

        ValidateAnswer(current, answer);
        var trustedAnswer = new VotingAnswer(answer.Kind, answer.ParticipantId, answer.ChoiceIndex, now);
        current.Record(playerId, trustedAnswer);
        TryAdvance(now);
        return trustedAnswer;
    }

    /// <summary>Convenience form for callers that already resolved the current slot.</summary>
    public VotingAnswer Answer(string playerId, VotingAnswer answer, DateTimeOffset now) =>
        Answer(playerId, CurrentSlot?.Slot ?? throw new InvalidOperationException("There is no voting slot open."), answer, now);

    /// <summary>Reads only the caller's answer for the current slot; no pending answer can leak.</summary>
    public VotingAnswer? AnswerFor(string playerId)
    {
        if (!IsParticipant(playerId) || CurrentSlot is not { } current) return null;
        return current.AnswerFor(playerId);
    }

    public VotingAnswer? PendingAnswer(string playerId) => AnswerFor(playerId);
    public VotingAnswer? CurrentAnswerFor(string playerId) => AnswerFor(playerId);

    /// <summary>Whether every active participant has answered the current slot.</summary>
    public bool CanAdvance => CurrentSlot is { } current && _participants.Where(IsActive).All(current.HasAnswered);

    /// <summary>
    /// Applies activity expiry first, then re-evaluates the answer barrier, then serves the next slot.
    /// The deadline is measured independently from each slot's served timestamp.
    /// </summary>
    public bool Advance(DateTimeOffset now)
    {
        if (State != MatchState.InProgress || CurrentSlot is not { } current) return false;

        var changed = false;
        foreach (var participantId in _participants.Where(IsActive).ToArray())
        {
            if (!current.HasAnswered(participantId) && now - current.ServedAt >= VotingRules.IdleAfter)
            {
                _inactive.Add(participantId);
                changed = true;
            }
        }

        if (ActiveParticipants.Count < 2)
        {
            Finish(MatchState.NoContest, now);
            return true;
        }

        return TryAdvance(now) || changed;
    }

    public VotingMatchSnapshot ToSnapshot() => new(
        Id, Code, [.. _participants], Capacity, [.. _questions.Select(ToQuestionSnapshot)],
        [.. _slots.Select(s => s.ToSnapshot())], _currentSlot, [.. _inactive], State, CreatedAt, EndedAt);

    /// <summary>Rehydrates trusted storage without applying authoring or state-machine validation.</summary>
    public static VotingMatch FromSnapshot(VotingMatchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var questions = (snapshot.Questions ?? []).Select(FromQuestionSnapshot).ToArray();
        var participants = snapshot.Participants ?? [];
        var match = new VotingMatch(snapshot.Id, snapshot.Code, participants.FirstOrDefault() ?? string.Empty,
            snapshot.Capacity, questions, snapshot.CreatedAt)
        {
            State = snapshot.State,
            EndedAt = snapshot.EndedAt,
            _currentSlot = snapshot.CurrentSlot
        };
        match._participants.Clear();
        match._participants.AddRange(participants);
        if (snapshot.InactiveParticipants is not null)
            foreach (var participantId in snapshot.InactiveParticipants) match._inactive.Add(participantId);
        if (snapshot.Slots is not null)
            match._slots.AddRange(snapshot.Slots.Select(VotingSlot.Restore));
        return match;
    }

    private bool TryAdvance(DateTimeOffset now)
    {
        if (State != MatchState.InProgress || !CanAdvance) return false;

        if (_currentSlot is not { } index || index >= _questions.Count - 1)
        {
            Finish(MatchState.Resolved, now);
            return true;
        }

        ServeNext(now);
        return true;
    }

    private void ServeNext(DateTimeOffset now)
    {
        var index = _slots.Count;
        var question = _questions[index];
        var options = question.AnswerSource == VotingAnswerSource.Participants
            ? _participants.Select(VotingServedOption.ForParticipant).ToList()
            : question.FixedChoices.Select((choice, choiceIndex) => VotingServedOption.ForChoice(choiceIndex, choice)).ToList();
        if (question.AnswerSource == VotingAnswerSource.Participants)
        {
            options.Add(VotingServedOption.MultipleParticipants());
            options.Add(VotingServedOption.NoParticipant());
        }
        else
        {
            options.Add(VotingServedOption.NotApplicable());
        }
        _slots.Add(new VotingSlot(index, question.Id, question.Prompt, options, question.Media, now));
        _currentSlot = index;
        question.RecordServed();
    }

    private void ValidateAnswer(VotingSlot slot, VotingAnswer answer)
    {
        switch (answer.Kind)
        {
            case VotingAnswerKind.SelectedParticipant:
                if (!slot.Options.Any(option => option.Kind == VotingAnswerKind.SelectedParticipant
                    && option.ParticipantId == answer.ParticipantId))
                    throw new InvalidOperationException("That participant is not an option for this slot.");
                break;
            case VotingAnswerKind.SelectedChoice:
                if (answer.ChoiceIndex is not { } choiceIndex || choiceIndex < 0
                    || choiceIndex >= slot.Options.Count
                    || slot.Options[choiceIndex].Kind != VotingAnswerKind.SelectedChoice)
                    throw new InvalidOperationException("That choice is not an option for this slot.");
                break;
            case VotingAnswerKind.NotApplicable:
                break;
            case VotingAnswerKind.MultipleParticipants:
            case VotingAnswerKind.NoParticipant:
                if (!slot.Options.Any(option => option.Kind == answer.Kind))
                    throw new InvalidOperationException("That answer is not an option for this slot.");
                break;
            default:
                throw new InvalidOperationException("The voting answer kind is not declared.");
        }
    }

    private void RequireActiveParticipant(string playerId)
    {
        if (!IsParticipant(playerId)) throw new InvalidOperationException("You are not a participant in this match.");
        if (!IsActive(playerId)) throw new InvalidOperationException("You are no longer active in this match.");
    }

    private bool IsActive(string playerId) => _participants.Contains(playerId) && !_inactive.Contains(playerId);
    public bool IsParticipant(string playerId) => _participants.Contains(playerId);
    public bool IsActiveParticipant(string playerId) => IsActive(playerId);

    private void Finish(MatchState state, DateTimeOffset now)
    {
        State = state;
        EndedAt = now;
        _currentSlot = null;
    }

    private static VotingQuestionSnapshot ToQuestionSnapshot(VotingQuestion question) => new(
        question.Id, question.Lang, question.CategoryId, question.Prompt, question.AnswerSource,
        [.. question.FixedChoices], question.Media, question.Status, question.Source, question.Topic,
        question.CreatedAt, question.UpdatedAt, question.TimesServed);

    private static VotingQuestion FromQuestionSnapshot(VotingQuestionSnapshot question) => VotingQuestion.Restore(
        question.Id, question.Lang, question.CategoryId, question.Prompt, question.AnswerSource,
        question.FixedChoices ?? [], question.Media, question.Status, question.Source, question.Topic,
        question.CreatedAt, question.UpdatedAt, question.TimesServed);
}
