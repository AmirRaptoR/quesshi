namespace Quesshi.Domain;

/// <summary>
/// The computed, read-only results of one matching match. The computation deliberately accepts the
/// persisted snapshot rather than a live aggregate: an endpoint can therefore use exactly the same
/// answer set after a grain reactivation, and no answer is made visible before the slot barrier has
/// closed.
/// </summary>
public sealed record MatchingResults(
    IReadOnlyList<MatchingSlotResult?> Slots,
    IReadOnlyList<MatchingPairStat>? PairStats,
    int? AllAgreedCount)
{
    /// <summary>Whether the whole-match figures are available to a caller.</summary>
    public bool HasOverallStatistics => PairStats is not null;

    /// <summary>Compatibility/readability alias for callers that call these pairwise figures pairs.</summary>
    public IReadOnlyList<MatchingPairStat>? Pairs => PairStats;

    /// <summary>Computes every result that is safe to expose from a persisted snapshot.</summary>
    public static MatchingResults Compute(MatchingMatchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var participants = (snapshot.Participants ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var activeParticipants = participants
            .Where(participant => !(snapshot.InactiveParticipants ?? []).Contains(participant))
            .ToArray();
        var slots = snapshot.Slots ?? [];
        var current = snapshot.CurrentSlot;
        var slotResults = new List<MatchingSlotResult?>(slots.Count);
        var closedResults = new List<(MatchingSlotSnapshot Slot, MatchingSlotResult Result)>();

        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            if (!IsClosed(snapshot, slot, index, current, slots.Count, participants))
            {
                slotResults.Add(null);
                continue;
            }

            var result = ComputeSlot(slot, participants);
            slotResults.Add(result);
            closedResults.Add((slot, result));
        }

        // A no-contest match is deliberately never summarised as a whole: some participants may
        // have been expired or have left with an unfinished slot. Resolved is the normal terminal
        // state and still checks the barrier explicitly so a malformed/legacy snapshot cannot leak
        // an apparently complete result early.
        var complete = snapshot.State == MatchState.Resolved
            && activeParticipants.Length > 0
            && slots.Count > 0
            && activeParticipants.All(participant => slots.All(slot => HasSubmitted(slot, participant)));

        if (!complete)
            return new MatchingResults(slotResults, null, null);

        var pairStats = new List<MatchingPairStat>();
        for (var first = 0; first < participants.Length; first++)
        {
            for (var second = first + 1; second < participants.Length; second++)
            {
                var same = 0;
                var different = 0;
                foreach (var (slot, _) in closedResults)
                {
                    var left = ChoiceFor(slot, participants[first]);
                    var right = ChoiceFor(slot, participants[second]);
                    if (left is null || right is null || left.Value.NotApplicable || right.Value.NotApplicable)
                        continue;

                    if (left.Value.Equals(right.Value)) same++;
                    else different++;
                }

                var comparable = same + different;
                var percent = comparable == 0
                    ? null
                    : (int?)Math.Round(100d * same / comparable, MidpointRounding.AwayFromZero);
                pairStats.Add(new MatchingPairStat(participants[first], participants[second], same,
                    different, percent));
            }
        }

        var allAgreed = closedResults.Count(pair => pair.Result.AllAgreed);
        return new MatchingResults(slotResults, pairStats, allAgreed);
    }

    private static bool IsClosed(MatchingMatchSnapshot snapshot, MatchingSlotSnapshot slot, int index,
        int? currentSlot, int slotCount, IReadOnlyList<string> participants)
    {
        if (snapshot.State == MatchState.Resolved) return true;

        // A running match keeps the current slot index. Every earlier served slot has necessarily
        // crossed its barrier. Once a no-contest match has ended, the final slot (if any) is the one
        // that was still open; prior slots had already advanced successfully.
        if (currentSlot is { } current) return slot.Slot < current;
        if (snapshot.State != MatchState.NoContest || slotCount == 0)
            return false;
        if (index < slotCount - 1) return true;

        // A hand-built/legacy snapshot can retain a no-contest state after the final barrier
        // closed. Treat that slot as closed when every roster member submitted; an unanswered
        // inactive member is the ordinary shape of a no-contest final slot and remains absent.
        return participants.Count > 0 && participants.All(participant => HasSubmitted(slot, participant));
    }

    private static MatchingSlotResult ComputeSlot(MatchingSlotSnapshot slot, IReadOnlyList<string> participants)
    {
        var counts = new int[slot.Options?.Count ?? 0];
        var choices = new List<Choice>(participants.Count);
        foreach (var participant in participants)
        {
            if (slot.Answers is null || !slot.Answers.TryGetValue(participant, out var answer)) continue;
            var choice = ChoiceFor(slot, answer);
            if (choice is null) continue;

            counts[choice.Value.OptionIndex]++;
            if (!choice.Value.NotApplicable) choices.Add(choice.Value);
        }

        var allAgreed = choices.Count >= 2 && choices.All(choice => choice.Equals(choices[0]));
        return new MatchingSlotResult(slot.Slot, [.. counts], allAgreed);
    }

    private static bool HasSubmitted(MatchingSlotSnapshot slot, string participant)
        => slot.Answers is not null && slot.Answers.ContainsKey(participant);

    private static Choice? ChoiceFor(MatchingSlotSnapshot slot, string participant)
    {
        if (slot.Answers is null || !slot.Answers.TryGetValue(participant, out var answer)) return null;
        return ChoiceFor(slot, answer);
    }

    private static Choice? ChoiceFor(MatchingSlotSnapshot slot, MatchingAnswer answer)
    {
        var options = slot.Options ?? [];
        if (answer.Kind == MatchingAnswerKind.NotApplicable)
        {
            var index = options.FindIndex(option => option.Kind == MatchingAnswerKind.NotApplicable);
            return index < 0 ? null : new Choice(index, true, null, null);
        }

        if (answer.Kind is MatchingAnswerKind.MultipleParticipants or MatchingAnswerKind.NoParticipant)
        {
            var index = options.FindIndex(option => option.Kind == answer.Kind);
            return index < 0 ? null : new Choice(index, false, null, null);
        }

        if (answer.Kind == MatchingAnswerKind.SelectedParticipant && answer.ParticipantId is { } participantId)
        {
            var index = options.FindIndex(option => option.Kind == MatchingAnswerKind.SelectedParticipant
                && option.ParticipantId == participantId);
            return index < 0 ? null : new Choice(index, false, participantId, null);
        }

        if (answer.Kind == MatchingAnswerKind.SelectedChoice && answer.ChoiceIndex is { } choiceIndex)
        {
            var index = options.FindIndex(option => option.Kind == MatchingAnswerKind.SelectedChoice
                && option.ChoiceIndex == choiceIndex);
            return index < 0 ? null : new Choice(index, false, null, choiceIndex);
        }

        return null;
    }

    private readonly record struct Choice(int OptionIndex, bool NotApplicable, string? ParticipantId,
        int? ChoiceIndex);
}

/// <summary>Counts and agreement for one closed matching slot, aligned to served option order.</summary>
public sealed record MatchingSlotResult(int Slot, IReadOnlyList<int> Counts, bool AllAgreed)
{
    public IReadOnlyList<int> OptionCounts => Counts;
}

/// <summary>Agreement between two participants over the slots both answered with a real option.</summary>
public sealed record MatchingPairStat(string FirstParticipantId, string SecondParticipantId, int Same,
    int Different, int? AgreementPercent)
{
    public string ParticipantA => FirstParticipantId;
    public string ParticipantB => SecondParticipantId;
}
