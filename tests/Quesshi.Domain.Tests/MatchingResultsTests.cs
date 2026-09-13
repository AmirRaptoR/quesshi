using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public sealed class MatchingResultsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Players = ["p1", "p2", "p3"];

    [Fact]
    public void Closed_participant_slot_counts_served_options_in_order_and_keeps_departed_answers()
    {
        var slot = ParticipantSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.SelectedParticipant("p2", T0),
            ["p2"] = MatchingAnswer.SelectedParticipant("p2", T0),
            ["p3"] = MatchingAnswer.NotApplicable(T0)
        });
        var snapshot = Snapshot([slot], state: MatchState.Resolved, inactive: ["p3"]);

        var results = MatchingResults.Compute(snapshot);
        var result = Assert.IsType<MatchingSlotResult>(Assert.Single(results.Slots));

        Assert.Equal([0, 2, 0, 1], result.Counts);
        Assert.True(result.AllAgreed);
    }

    [Fact]
    public void Not_applicable_is_counted_in_its_served_position_but_never_agrees()
    {
        var slot = ParticipantSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.NotApplicable(T0),
            ["p2"] = MatchingAnswer.NotApplicable(T0),
            ["p3"] = MatchingAnswer.NotApplicable(T0)
        });
        var results = MatchingResults.Compute(Snapshot([slot], state: MatchState.Resolved));

        var result = Assert.IsType<MatchingSlotResult>(Assert.Single(results.Slots));
        Assert.Equal([0, 0, 0, 3], result.Counts);
        Assert.False(result.AllAgreed);
    }

    [Fact]
    public void A_slot_with_one_real_answer_is_not_all_agreed()
    {
        var slot = ParticipantSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.SelectedParticipant("p2", T0),
            ["p2"] = MatchingAnswer.NotApplicable(T0)
        });
        var results = MatchingResults.Compute(Snapshot([slot], state: MatchState.Resolved));

        Assert.False(Assert.IsType<MatchingSlotResult>(Assert.Single(results.Slots)).AllAgreed);
    }

    [Fact]
    public void Open_slot_is_absent_and_has_no_zero_filled_distribution()
    {
        var closed = ParticipantSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.SelectedParticipant("p1", T0),
            ["p2"] = MatchingAnswer.SelectedParticipant("p2", T0)
        }, slot: 0);
        var open = ParticipantSlot(new Dictionary<string, MatchingAnswer>(), slot: 1);
        var snapshot = Snapshot([closed, open], state: MatchState.InProgress, currentSlot: 1);

        var results = MatchingResults.Compute(snapshot);

        Assert.NotNull(results.Slots[0]);
        Assert.Null(results.Slots[1]);
        Assert.Null(results.PairStats);
        Assert.Null(results.AllAgreedCount);
    }

    [Fact]
    public void Pair_stats_use_participant_ids_for_participant_slots_and_indices_for_fixed_slots()
    {
        var participant = ParticipantSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.SelectedParticipant("p2", T0),
            ["p2"] = MatchingAnswer.SelectedParticipant("p2", T0),
            ["p3"] = MatchingAnswer.NotApplicable(T0)
        }, slot: 0);
        var fixedSlot = FixedSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.SelectedChoice(0, T0),
            ["p2"] = MatchingAnswer.SelectedChoice(1, T0),
            ["p3"] = MatchingAnswer.SelectedChoice(0, T0)
        }, slot: 1);

        var results = MatchingResults.Compute(Snapshot([participant, fixedSlot], state: MatchState.Resolved));

        Assert.Equal(3, results.PairStats!.Count);
        Assert.Equal(new MatchingPairStat("p1", "p2", 1, 1, 50), results.PairStats[0]);
        Assert.Equal(new MatchingPairStat("p1", "p3", 1, 0, 100), results.PairStats[1]);
        Assert.Equal(new MatchingPairStat("p2", "p3", 0, 1, 0), results.PairStats[2]);
        Assert.Equal(1, results.AllAgreedCount);
    }

    [Fact]
    public void Pair_stats_are_deterministic_and_round_half_away_from_zero()
    {
        var slots = Enumerable.Range(0, 3).Select(index => ParticipantSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.SelectedParticipant("p1", T0),
            ["p2"] = index == 2
                ? MatchingAnswer.SelectedParticipant("p2", T0)
                : MatchingAnswer.SelectedParticipant("p1", T0),
            ["p3"] = MatchingAnswer.NotApplicable(T0)
        }, index)).ToList();

        var results = MatchingResults.Compute(Snapshot(slots, state: MatchState.Resolved));
        var pairs = results.PairStats!;

        Assert.Equal(["p1", "p1", "p2"], pairs.Select(pair => pair.FirstParticipantId));
        Assert.Equal(["p2", "p3", "p3"], pairs.Select(pair => pair.SecondParticipantId));
        Assert.Equal(2, pairs[0].Same);
        Assert.Equal(1, pairs[0].Different);
        Assert.Equal(67, pairs[0].AgreementPercent);
    }

    [Fact]
    public void Agreement_percent_rounds_a_half_up_away_from_zero()
    {
        var slots = Enumerable.Range(0, 8).Select(index => ParticipantSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.SelectedParticipant("p1", T0),
            ["p2"] = index == 0
                ? MatchingAnswer.SelectedParticipant("p1", T0)
                : MatchingAnswer.SelectedParticipant("p2", T0),
            ["p3"] = MatchingAnswer.NotApplicable(T0)
        }, index)).ToList();

        var result = MatchingResults.Compute(Snapshot(slots, state: MatchState.Resolved));

        Assert.Equal(13, Assert.Single(result.PairStats!, pair => pair.FirstParticipantId == "p1"
            && pair.SecondParticipantId == "p2").AgreementPercent);
    }

    [Fact]
    public void Missing_answers_contribute_to_neither_counts_nor_pairwise_comparisons()
    {
        var slot = ParticipantSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.SelectedParticipant("p1", T0),
            ["p2"] = MatchingAnswer.SelectedParticipant("p1", T0)
        });
        var snapshot = Snapshot([slot], state: MatchState.Resolved, inactive: ["p3"]);

        var results = MatchingResults.Compute(snapshot);

        var slotResult = Assert.IsType<MatchingSlotResult>(Assert.Single(results.Slots));
        Assert.Equal([2, 0, 0, 0], slotResult.Counts);
        Assert.True(slotResult.AllAgreed);
        var p1p2 = Assert.Single(results.PairStats!, pair => pair.FirstParticipantId == "p1" && pair.SecondParticipantId == "p2");
        Assert.Equal(1, p1p2.Same);
        Assert.Null(Assert.Single(results.PairStats!, pair => pair.FirstParticipantId == "p1"
            && pair.SecondParticipantId == "p3").AgreementPercent);
        Assert.Null(Assert.Single(results.PairStats!, pair => pair.FirstParticipantId == "p2"
            && pair.SecondParticipantId == "p3").AgreementPercent);
    }

    [Fact]
    public void No_contest_exposes_only_slots_closed_before_termination_and_never_overall_stats()
    {
        var closed = ParticipantSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.SelectedParticipant("p1", T0),
            ["p2"] = MatchingAnswer.SelectedParticipant("p1", T0)
        }, slot: 0);
        var unfinished = ParticipantSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.SelectedParticipant("p2", T0)
        }, slot: 1);

        var results = MatchingResults.Compute(Snapshot([closed, unfinished], state: MatchState.NoContest));

        Assert.NotNull(results.Slots[0]);
        Assert.Null(results.Slots[1]);
        Assert.Null(results.PairStats);
        Assert.Null(results.AllAgreedCount);
    }

    [Fact]
    public void No_contest_with_no_served_slots_has_nothing_to_summarise()
    {
        var results = MatchingResults.Compute(Snapshot([], state: MatchState.NoContest));

        Assert.Empty(results.Slots);
        Assert.Null(results.PairStats);
        Assert.Null(results.AllAgreedCount);
    }

    [Fact]
    public void A_fully_answered_final_slot_remains_visible_even_if_a_legacy_snapshot_marks_no_contest()
    {
        var slot = ParticipantSlot(new Dictionary<string, MatchingAnswer>
        {
            ["p1"] = MatchingAnswer.SelectedParticipant("p1", T0),
            ["p2"] = MatchingAnswer.SelectedParticipant("p1", T0),
            ["p3"] = MatchingAnswer.NotApplicable(T0)
        });

        var results = MatchingResults.Compute(Snapshot([slot], state: MatchState.NoContest));

        Assert.NotNull(results.Slots[0]);
        Assert.Null(results.PairStats);
    }

    private static MatchingMatchSnapshot Snapshot(IReadOnlyList<MatchingSlotSnapshot> slots,
        MatchState state, int? currentSlot = null, IReadOnlyCollection<string>? inactive = null)
        => new("match", "code", [.. Players], Players.Length, [], [.. slots], currentSlot,
            [.. inactive ?? []], state, T0, state is MatchState.Resolved or MatchState.NoContest ? T0 : null);

    private static MatchingSlotSnapshot ParticipantSlot(Dictionary<string, MatchingAnswer> answers,
        int slot = 0)
        => new(slot, $"q-{slot}", $"Prompt {slot}",
            [MatchingServedOption.ForParticipant("p1"), MatchingServedOption.ForParticipant("p2"),
             MatchingServedOption.ForParticipant("p3"), MatchingServedOption.NotApplicable()],
            T0, answers);

    private static MatchingSlotSnapshot FixedSlot(Dictionary<string, MatchingAnswer> answers, int slot)
        => new(slot, $"q-{slot}", $"Prompt {slot}",
            [MatchingServedOption.ForChoice(0, "A"), MatchingServedOption.ForChoice(1, "B"),
             MatchingServedOption.NotApplicable()], T0, answers);
}
