using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Grains;

/// <summary>
/// Persistent Orleans adapter for <see cref="MatchingMatch"/>. Lobby metadata is kept separately
/// until Start has resolved the question set; from that point on the complete matching snapshot is
/// the durable source of truth. SignalR is deliberately best effort: persistence and archive writes
/// complete before either push is attempted.
/// </summary>
public sealed class MatchingMatchGrain(
    [PersistentState("matching-match", "hot")] IPersistentState<MatchingMatchStateRecord> state,
    MatchingQuestionSetBuilder questionSetBuilder,
    IMatchingQuestionRepository questions,
    IMatchArchive archive,
    IMatchingNotifier notifier,
    IClock clock,
    ILogger<MatchingMatchGrain> logger) : Grain, IMatchingMatchGrain, IRemindable
{
    private const string Reminder = "matching-idle";
    private MatchingMatch? _match;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        state.State.ServedQuestionPending ??= [];
        if (!string.IsNullOrWhiteSpace(state.State.Json))
            _match = MatchingMatch.FromSnapshot(JsonSerializer.Deserialize<MatchingMatchSnapshot>(state.State.Json)!);
        if (state.State.ArchivePending)
            await ReconcileArchiveAsync();
        await ReconcileServedQuestionCountsAsync();
        if (_match?.IsOver == true)
            await UnregisterReminderSafeAsync();
    }

    public async Task<MatchingView> CreateAsync(string code, string ownerId, int lang, int questionCount,
        List<string> categoryIds, int capacity)
    {
        if (!string.IsNullOrWhiteSpace(state.State.Code))
            return ViewFor(ownerId);

        state.State.Code = code;
        state.State.OwnerId = ownerId;
        state.State.Lang = lang;
        state.State.QuestionCount = questionCount;
        state.State.CategoryIds = [.. categoryIds.Distinct()];
        state.State.Capacity = capacity;
        state.State.Participants = [ownerId];
        state.State.CreatedAt = clock.Now;
        state.State.State = (int)MatchState.AwaitingOpponent;
        try
        {
            await SaveAndArchiveAsync();
        }
        catch (MatchCodeCollisionException)
        {
            // SaveAndArchiveAsync writes Orleans state before the shared archive. If the archive's
            // unique code index loses a race, remove that state before allowing the endpoint to retry;
            // otherwise the failed attempt leaves an unresolvable matching grain behind.
            _match = null;
            await state.ClearStateAsync();
            state.State = new MatchingMatchStateRecord();
            // Keep the grain boundary on Orleans' built-in exception types. The archive-specific
            // exception is an application/infrastructure detail and Orleans cannot serialize it as a
            // remoted exception without registering a codec for the infrastructure assembly.
            throw new InvalidOperationException("matching_code_collision");
        }
        return ViewFor(ownerId);
    }

    public async Task<int> JoinAsync(string playerId)
    {
        if (string.IsNullOrWhiteSpace(state.State.Code)) return (int)MatchingJoinResult.NotFound;
        if ((MatchState)state.State.State != MatchState.AwaitingOpponent)
            return (int)MatchingJoinResult.Started;
        if (state.State.Participants.Contains(playerId)) return (int)MatchingJoinResult.AlreadyIn;
        if (state.State.Participants.Count >= state.State.Capacity)
            return (int)MatchingJoinResult.Full;

        state.State.Participants.Add(playerId);
        await SaveAndArchiveAsync();
        await SafeNotifyAsync(() => notifier.RosterChangedAsync(IdString()));
        return (int)MatchingJoinResult.Joined;
    }

    public async Task<bool> StartAsync(string playerId)
    {
        if (string.IsNullOrWhiteSpace(state.State.Code)
            || (MatchState)state.State.State != MatchState.AwaitingOpponent
            || playerId != state.State.OwnerId || state.State.Participants.Count < 2)
            return false;

        IReadOnlyList<MatchingQuestion> questions;
        try
        {
            questions = await questionSetBuilder.BuildAsync((Language)state.State.Lang,
                state.State.CategoryIds, state.State.QuestionCount);
        }
        catch (NotEnoughQuestionsException ex)
        {
            // Keep the Orleans boundary serializable while preserving the builder's actionable
            // message for the HTTP 503 response.
            throw new InvalidOperationException($"matching_not_enough_questions:{ex.Message}");
        }
        var match = MatchingMatch.Create(IdString(), state.State.Code, state.State.OwnerId,
            state.State.Capacity, [.. questions], state.State.CreatedAt);
        foreach (var participant in state.State.Participants.Skip(1))
            match.Join(participant, state.State.CreatedAt);
        if (!match.Start(playerId, clock.Now)) return false;

        _match = match;
        state.State.State = (int)match.State;
        TrackNewlyServedQuestions([], match.ToSnapshot());
        await SaveAndArchiveAsync();
        await this.RegisterOrUpdateReminder(Reminder, MatchingRules.IdleAfter, TimeSpan.FromHours(6));
        await SafeNotifyAsync(() => notifier.RosterChangedAsync(IdString()));
        return true;
    }

    public async Task<bool> LeaveAsync(string playerId)
    {
        if (string.IsNullOrWhiteSpace(state.State.Code)) return false;

        if (_match is null)
        {
            if ((MatchState)state.State.State != MatchState.AwaitingOpponent
                || !state.State.Participants.Contains(playerId)) return false;

            if (playerId == state.State.OwnerId)
            {
                state.State.State = (int)MatchState.NoContest;
                state.State.EndedAt = clock.Now;
            }
            else
            {
                state.State.Participants.Remove(playerId);
            }

            await SaveAndArchiveAsync();
            if ((MatchState)state.State.State == MatchState.NoContest)
                await UnregisterReminderSafeAsync();
            else
                await SafeNotifyAsync(() => notifier.RosterChangedAsync(IdString()));
            return true;
        }

        var before = _match.ToSnapshot();
        var changed = _match.Leave(playerId, clock.Now);
        if (!changed) return false;
        TrackNewlyServedQuestions(before.Slots, _match.ToSnapshot());
        await SaveAndArchiveAsync();
        if (_match.IsOver) await UnregisterReminderSafeAsync();
        await SafeNotifyAsync(() => notifier.RosterChangedAsync(IdString()));
        return true;
    }

    public async Task<bool> UpdateSettingsAsync(string playerId, int lang, int questionCount,
        List<string> categoryIds, List<int> levels, int? capacity, int mode)
    {
        if (mode != (int)GameMode.Matching || levels.Count > 0) return false;
        if (string.IsNullOrWhiteSpace(state.State.Code)
            || (MatchState)state.State.State != MatchState.AwaitingOpponent
            || playerId != state.State.OwnerId)
            return false;
        if (!MatchRules.IsValidCount(questionCount)) return false;
        if (capacity is { } newCapacity && (newCapacity < 2 || newCapacity > MatchRules.MaxParticipants
            || newCapacity < state.State.Participants.Count)) return false;

        state.State.Lang = lang;
        state.State.QuestionCount = questionCount;
        state.State.CategoryIds = [.. categoryIds.Distinct()];
        if (capacity is { } c) state.State.Capacity = c;
        await SaveAndArchiveAsync();
        await SafeNotifyAsync(() => notifier.RosterChangedAsync(IdString()));
        return true;
    }

    public async Task<MatchingView> AnswerAsync(string playerId, int slot, int kind,
        string? participantId, int? choiceIndex)
    {
        if (string.IsNullOrWhiteSpace(state.State.Code))
            throw new InvalidOperationException("match_not_found");
        if (_match is null)
            throw new InvalidOperationException("match_not_started");
        if (!_match.IsParticipant(playerId)) throw new InvalidOperationException("not_a_participant");
        if (!_match.IsActiveParticipant(playerId)) throw new InvalidOperationException("participant_left");
        if (_match.IsOver) throw new InvalidOperationException("match_over");
        if (_match.CurrentSlot is null) throw new InvalidOperationException("match_not_started");
        if (_match.CurrentSlot.Slot != slot)
            throw new InvalidOperationException(slot < _match.CurrentSlot.Slot ? "answers_locked" : "stale_slot");
        if (!Enum.IsDefined(typeof(MatchingAnswerKind), kind)) throw new InvalidOperationException("bad_answer_kind");

        var answerKind = (MatchingAnswerKind)kind;
        if (answerKind == MatchingAnswerKind.SelectedParticipant && participantId is null)
            throw new InvalidOperationException("missing_field");
        if (answerKind == MatchingAnswerKind.SelectedChoice && choiceIndex is null)
            throw new InvalidOperationException("missing_field");
        if ((answerKind is MatchingAnswerKind.NotApplicable or MatchingAnswerKind.MultipleParticipants
                or MatchingAnswerKind.NoParticipant)
            && (participantId is not null || choiceIndex is not null))
            throw new InvalidOperationException("contradictory_fields");
        if (answerKind == MatchingAnswerKind.SelectedParticipant && choiceIndex is not null)
            throw new InvalidOperationException("contradictory_fields");
        if (answerKind == MatchingAnswerKind.SelectedChoice && participantId is not null)
            throw new InvalidOperationException("contradictory_fields");

        var before = _match.ToSnapshot();
        var source = before.Questions.FirstOrDefault(q => q.Id == _match.CurrentSlot.QuestionId)?.AnswerSource;
        if (answerKind == MatchingAnswerKind.SelectedParticipant && source != MatchingAnswerSource.Participants)
            throw new InvalidOperationException("wrong_answer_kind");
        if (answerKind == MatchingAnswerKind.SelectedChoice && source != MatchingAnswerSource.Fixed)
            throw new InvalidOperationException("wrong_answer_kind");
        MatchingAnswer answer = answerKind switch
        {
            MatchingAnswerKind.SelectedParticipant => MatchingAnswer.SelectedParticipant(participantId!, clock.Now),
            MatchingAnswerKind.SelectedChoice => MatchingAnswer.SelectedChoice(choiceIndex!.Value, clock.Now),
            MatchingAnswerKind.NotApplicable => MatchingAnswer.NotApplicable(clock.Now),
            MatchingAnswerKind.MultipleParticipants => MatchingAnswer.MultipleParticipants(clock.Now),
            MatchingAnswerKind.NoParticipant => MatchingAnswer.NoParticipant(clock.Now),
            _ => throw new InvalidOperationException("bad_answer_kind")
        };

        try
        {
            _match.Answer(playerId, slot, answer, clock.Now);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(MapAnswerError(ex.Message), ex);
        }
        TrackNewlyServedQuestions(before.Slots, _match.ToSnapshot());

        await SaveAndArchiveAsync();
        var after = _match.ToSnapshot();
        if (before.CurrentSlot is { } beforeSlot && after.CurrentSlot != before.CurrentSlot)
        {
            var closed = after.Slots.FirstOrDefault(s => s.Slot == beforeSlot);
            if (closed is not null)
                await SafeNotifyAsync(() => notifier.SlotClosedAsync(IdString(),
                    new MatchingSlotClosedPush(closed.Slot,
                        _match.IsOver
                            ? [.. closed.Answers.Select(kv => new MatchingAnswerPush(kv.Key, (int)kv.Value.Kind,
                                kv.Value.ParticipantId, kv.Value.ChoiceIndex))]
                            : [])));
        }
        if (_match.IsOver) await UnregisterReminderSafeAsync();
        return ViewFor(playerId);
    }

    public Task<MatchingView?> GetAsync(string playerId)
    {
        if (string.IsNullOrWhiteSpace(state.State.Code)) return Task.FromResult<MatchingView?>(null);
        var currentState = (MatchState)state.State.State;
        // An open lobby is intentionally discoverable by code, but once questions have been served
        // only a seated participant may read the match. This keeps closed answers and computed
        // statistics private even after the match has resolved or ended no-contest.
        if (_match is not null && !_match.IsParticipant(playerId))
            return Task.FromResult<MatchingView?>(null);
        return Task.FromResult<MatchingView?>(ViewFor(playerId));
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (_match is null) return;
        if (_match.IsOver)
        {
            await UnregisterReminderSafeAsync();
            return;
        }
        var before = _match.ToSnapshot();
        if (!_match.Advance(clock.Now)) return;
        var after = _match.ToSnapshot();
        TrackNewlyServedQuestions(before.Slots, after);

        await SaveAndArchiveAsync();
        if (!before.InactiveParticipants.SetEquals(after.InactiveParticipants))
            await SafeNotifyAsync(() => notifier.RosterChangedAsync(IdString()));
        if (before.CurrentSlot is { } oldSlot && after.CurrentSlot != oldSlot)
        {
            var closed = after.Slots.FirstOrDefault(s => s.Slot == oldSlot);
            if (closed is not null)
                await SafeNotifyAsync(() => notifier.SlotClosedAsync(IdString(),
                    new MatchingSlotClosedPush(closed.Slot,
                        _match.IsOver
                            ? [.. closed.Answers.Select(kv => new MatchingAnswerPush(kv.Key, (int)kv.Value.Kind,
                                kv.Value.ParticipantId, kv.Value.ChoiceIndex))]
                            : [])));
        }
        if (_match.IsOver) await UnregisterReminderSafeAsync();
    }

    private async Task SaveAndArchiveAsync()
    {
        if (_match is not null)
        {
            state.State.Json = JsonSerializer.Serialize(_match.ToSnapshot());
            state.State.Participants = [.. _match.Participants];
            state.State.State = (int)_match.State;
            state.State.EndedAt = _match.EndedAt;
        }

        // The marker is part of the same durable write as the hot state. If the process dies after
        // this write, activation has enough information to replay the archive write. ReplaceOne
        // makes that replay idempotent, and leaving the marker set on an archive exception keeps a
        // later activation from silently losing the mirror update.
        state.State.ArchivePending = true;
        await state.WriteStateAsync();
        await archive.SaveAsync(ToArchive());

        // Clearing the marker is deliberately a second durable write. If this write is interrupted,
        // activation simply repeats the already-successful archive replacement.
        state.State.ArchivePending = false;
        await state.WriteStateAsync();
        await ReconcileServedQuestionCountsAsync();
    }

    private void TrackNewlyServedQuestions(IReadOnlyList<MatchingSlotSnapshot> before,
        MatchingMatchSnapshot after)
    {
        state.State.ServedQuestionPending ??= [];
        foreach (var slot in after.Slots.Skip(before.Count))
        {
            var question = after.Questions.FirstOrDefault(question => question.Id == slot.QuestionId);
            if (question is null) continue;

            // Slot numbers are stable for the lifetime of this match and the match id is globally
            // unique. Never collapse two concurrent matches' serves into one question-level target.
            state.State.ServedQuestionPending[$"{after.Id}:{slot.Slot}"] = question.Id;
        }
    }

    /// <summary>
    /// Applies the durable per-slot outbox to the content repository. The repository's atomic
    /// add-token-plus-increment operation makes replay safe and prevents concurrent matches from
    /// losing one of their increments.
    /// </summary>
    private async Task ReconcileServedQuestionCountsAsync()
    {
        state.State.ServedQuestionPending ??= [];
        if (state.State.ServedQuestionPending.Count == 0) return;

        var completed = new List<string>();
        foreach (var (serveToken, questionId) in state.State.ServedQuestionPending.ToArray())
        {
            try
            {
                var result = await questions.RecordServedAsync(questionId, serveToken);
                if (result is MatchingServeResult.Recorded or MatchingServeResult.AlreadyRecorded)
                    completed.Add(serveToken);
                else
                    logger.LogWarning("Matching served-question {QuestionId} is missing", questionId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Matching served-question counter reconciliation failed for {QuestionId}", questionId);
            }
        }

        if (completed.Count == 0) return;
        foreach (var serveToken in completed)
            state.State.ServedQuestionPending.Remove(serveToken);
        await state.WriteStateAsync();
    }

    private async Task ReconcileArchiveAsync()
    {
        try
        {
            await archive.SaveAsync(ToArchive());
            state.State.ArchivePending = false;
            await state.WriteStateAsync();
        }
        catch (MatchCodeCollisionException ex)
        {
            // A collision can only mean this grain was the losing create attempt. Preserve the
            // create path's cleanup semantics if a process stopped between the collision and clear.
            logger.LogWarning(ex, "Clearing orphaned matching grain {MatchId} after archive code collision", IdString());
            _match = null;
            await state.ClearStateAsync();
            state.State = new MatchingMatchStateRecord();
        }
        catch (Exception ex)
        {
            // Activation remains usable even when the archive is temporarily unavailable. The
            // durable marker stays true, so a later activation can retry without losing the state.
            logger.LogWarning(ex, "Matching archive reconciliation failed for {MatchId}", IdString());
        }
    }

    private ArchivedMatch ToArchive()
    {
        var participants = state.State.Participants;
        var results = participants.Select(id => new ParticipantResult(id, 0, 0, MatchOutcome.Loss)).ToList();
        return new ArchivedMatch(IdString(), state.State.Code, (Language)state.State.Lang,
            participants.FirstOrDefault() ?? state.State.OwnerId, participants.Skip(1).FirstOrDefault(),
            null, false, results, (MatchState)state.State.State, state.State.CreatedAt, state.State.EndedAt,
            _match?.ToSnapshot().Questions.Select(q => q.Id).ToList() ?? [], false, GameMode.Matching);
    }

    private MatchingView ViewFor(string playerId)
    {
        var stateValue = (MatchState)state.State.State;
        if (_match is null)
            return new MatchingView(IdString(), state.State.Code, state.State.Lang, state.State.Capacity,
                (int)stateValue, [.. state.State.Participants.Select(id => new MatchingParticipantView(id, true))],
                null, state.State.QuestionCount, null, null, null, state.State.CreatedAt, state.State.EndedAt,
                null, [.. state.State.CategoryIds], []);

        var snapshot = _match.ToSnapshot();
        var current = _match.CurrentSlot is { } active ? snapshot.Slots.FirstOrDefault(s => s.Slot == active.Slot) : null;
        var reveal = _match.IsOver && _match.IsParticipant(playerId);
        var computed = reveal ? MatchingResults.Compute(snapshot) : null;
        var closedSnapshots = computed is null
            ? []
            : snapshot.Slots
                .Select((slot, index) => (slot, index))
                .Where(item => item.index < computed.Slots.Count && computed.Slots[item.index] is not null)
                .Select(item => item.slot)
                .ToList();
        var closed = closedSnapshots.LastOrDefault();
        var own = current?.Answers?.GetValueOrDefault(playerId);
        var results = reveal ? ResultsView(snapshot) : null;
        return new MatchingView(_match.Id, _match.Code, state.State.Lang, _match.Capacity, (int)_match.State,
            [.. _match.Participants.Select(id => new MatchingParticipantView(id, _match.IsActiveParticipant(id)))],
            _match.CurrentSlotIndex, snapshot.Questions.Count, SlotView(current, includeAnswers: false),
            SlotView(closed, includeAnswers: true), AnswerView(playerId, own), _match.CreatedAt, _match.EndedAt,
            results, null, [.. closedSnapshots.Select(slot => SlotView(slot, includeAnswers: true)!) ]);
    }

    private static MatchingResultsView ResultsView(MatchingMatchSnapshot snapshot)
    {
        var results = MatchingResults.Compute(snapshot);
        return new MatchingResultsView(
            [.. results.Slots.Select(slot => slot is null ? null
                : new MatchingSlotResultView(slot.Slot, [.. slot.Counts], slot.AllAgreed))],
            results.PairStats is null ? null : [.. results.PairStats.Select(pair =>
                new MatchingPairStatView(pair.FirstParticipantId, pair.SecondParticipantId, pair.Same,
                    pair.Different, pair.AgreementPercent))],
            results.AllAgreedCount);
    }

    private static MatchingSlotView? SlotView(MatchingSlotSnapshot? slot, bool includeAnswers)
    {
        if (slot is null) return null;
        return new MatchingSlotView(slot.Slot, slot.QuestionId, slot.Prompt,
            [.. slot.Options.Select(o => new MatchingOptionView((int)o.Kind, o.ParticipantId, o.ChoiceIndex, o.Text))],
            slot.ServedAt, [.. slot.Answers.Keys], includeAnswers
                ? [.. slot.Answers.Select(answer => AnswerView(answer.Key, answer.Value)!) ]
                : [], slot.Media is { Kind: not MediaKind.None } media
                    ? new MatchingMediaView((int)media.Kind, media.Url, media.Attribution)
                    : null);
    }

    private static MatchingAnswerView? AnswerView(string? playerId, MatchingAnswer? answer)
        => answer is null ? null : new MatchingAnswerView((int)answer.Kind, answer.ParticipantId,
            answer.ChoiceIndex, answer.At, playerId);

    private static string MapAnswerError(string message) => message switch
    {
        "You are not a participant in this match." => "not_a_participant",
        "You are no longer active in this match." => "participant_left",
        "That participant is not an option for this slot." => "unknown_participant",
        "That choice is not an option for this slot." => "bad_choice_index",
        "The matching answer kind is not declared." => "bad_answer_kind",
        _ => message.Contains("no matching slot", StringComparison.OrdinalIgnoreCase)
            ? "stale_slot" : "answers_locked"
    };

    private async Task SafeNotifyAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { logger.LogWarning(ex, "Matching push failed for {MatchId}", IdString()); }
    }

    private async Task UnregisterReminderSafeAsync()
    {
        try
        {
            if (await this.GetReminder(Reminder) is { } registered)
                await this.UnregisterReminder(registered);
        }
        catch (Exception ex) { logger.LogDebug(ex, "Matching reminder was already absent for {MatchId}", IdString()); }
    }

    private string IdString() => this.GetPrimaryKeyString();
}
