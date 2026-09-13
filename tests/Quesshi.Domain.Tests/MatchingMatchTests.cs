using System.Reflection;
using System.Text.Json;
using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchingMatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private const string Owner = "p-owner";
    private const string Other = "p-other";
    private const string Third = "p-third";

    private static MatchingQuestion ParticipantsQuestion(string id = "mq-1", string prompt = "Prompt") =>
        MatchingQuestion.Create(id, Language.En, "m-general", prompt, MatchingAnswerSource.Participants,
            null, T0, status: QuestionStatus.Approved);

    private static MatchingQuestion FixedQuestion(string id = "mq-1", string prompt = "Prompt") =>
        MatchingQuestion.Create(id, Language.En, "m-general", prompt, MatchingAnswerSource.Fixed,
            ["One", "Two"], T0, status: QuestionStatus.Approved);

    private static MatchingMatch NewMatch(params MatchingQuestion[] questions)
    {
        var match = MatchingMatch.Create("mm-1", "MCODE", Owner, 3,
            questions.Length == 0 ? [ParticipantsQuestion()] : questions, T0);
        Assert.True(match.Join(Other, T0));
        return match;
    }

    [Fact]
    public void Answer_shape_has_no_sentinel_and_rejects_contradictory_payloads()
    {
        Assert.Equal(MatchingAnswerKind.SelectedParticipant, new MatchingAnswer(
            MatchingAnswerKind.SelectedParticipant, Owner, null, T0).Kind);
        Assert.Null(new MatchingAnswer(MatchingAnswerKind.NotApplicable, null, null, T0).ChoiceIndex);

        Assert.Throws<ArgumentException>(() => new MatchingAnswer(
            MatchingAnswerKind.SelectedParticipant, null, null, T0));
        Assert.Throws<ArgumentException>(() => new MatchingAnswer(
            MatchingAnswerKind.SelectedParticipant, Owner, 0, T0));
        Assert.Throws<ArgumentException>(() => new MatchingAnswer(
            MatchingAnswerKind.SelectedChoice, null, null, T0));
        Assert.Throws<ArgumentException>(() => new MatchingAnswer(
            MatchingAnswerKind.SelectedChoice, Owner, 0, T0));
        Assert.Throws<ArgumentException>(() => new MatchingAnswer(
            MatchingAnswerKind.NotApplicable, Owner, null, T0));
        Assert.Throws<ArgumentException>(() => new MatchingAnswer(
            MatchingAnswerKind.NotApplicable, null, 0, T0));
    }

    [Fact]
    public void Start_serves_slot_zero_and_snapshots_participant_options_in_join_order()
    {
        var match = NewMatch(ParticipantsQuestion());

        Assert.Null(match.CurrentSlot);
        Assert.True(match.Start(Owner, T0));

        var slot = Assert.IsType<MatchingSlot>(match.CurrentSlot);
        Assert.Equal(0, slot.Slot);
        Assert.Equal("Prompt", slot.Prompt);
        Assert.Equal([Owner, Other], slot.Options.Take(2).Select(x => x.ParticipantId));
        Assert.Equal(MatchingAnswerKind.NotApplicable, slot.Options[^1].Kind);
        Assert.All(slot.Options.Take(2), option => Assert.Null(option.Text));
    }

    [Fact]
    public void Fixed_options_keep_their_authored_order_and_not_applicable_is_not_a_choice()
    {
        var match = NewMatch(FixedQuestion());
        Assert.True(match.Start(Owner, T0));
        var options = match.CurrentSlot!.Options;

        Assert.Equal(["One", "Two"], options.Take(2).Select(x => x.Text));
        Assert.Equal([0, 1], options.Take(2).Select(x => x.ChoiceIndex));
        Assert.Equal(MatchingAnswerKind.NotApplicable, options[^1].Kind);
        Assert.Throws<InvalidOperationException>(() => match.Answer(Owner, 0,
            MatchingAnswer.SelectedChoice(2, T0)));
    }

    [Fact]
    public void A_served_slot_is_immutable_when_the_source_question_changes()
    {
        var question = ParticipantsQuestion();
        var match = NewMatch(question);
        Assert.True(match.Start(Owner, T0));

        question.Edit(Language.En, "m-general", "Changed", MatchingAnswerSource.Fixed, ["A", "B"],
            null, "topic", T0.AddHours(1));

        Assert.Equal("Prompt", match.CurrentSlot!.Prompt);
        Assert.Equal([Owner, Other], match.CurrentSlot.Options.Take(2).Select(x => x.ParticipantId));
    }

    [Fact]
    public void Final_answer_closes_the_barrier_and_serves_the_next_slot_in_the_same_call()
    {
        var match = NewMatch(ParticipantsQuestion("mq-1"), ParticipantsQuestion("mq-2", "Second"));
        Assert.True(match.Start(Owner, T0));

        match.Answer(Owner, 0, MatchingAnswer.SelectedParticipant(Owner, T0));
        Assert.Equal(0, match.CurrentSlot!.Slot);
        Assert.False(match.CanAdvance);
        Assert.Null(match.AnswerFor(Other));

        match.Answer(Other, 0, MatchingAnswer.NotApplicable(T0.AddMinutes(1)));

        Assert.Equal(1, match.CurrentSlot!.Slot);
        Assert.Equal(2, match.Slots.Count);
        Assert.True(match.AnswerFor(Owner) is null);
        Assert.Equal(MatchingAnswerKind.NotApplicable,
            match.ToSnapshot().Slots[0].Answers[Other].Kind);
    }

    [Fact]
    public void A_pending_answer_can_be_replaced_until_the_barrier_but_old_slot_is_locked_afterwards()
    {
        var match = NewMatch(ParticipantsQuestion("mq-1"), ParticipantsQuestion("mq-2"));
        Assert.True(match.Start(Owner, T0));
        var first = MatchingAnswer.SelectedParticipant(Owner, T0.AddMinutes(1));
        var second = MatchingAnswer.NotApplicable(T0.AddMinutes(2));

        match.Answer(Owner, 0, first);
        match.Answer(Owner, 0, second);
        Assert.Equal(second, match.AnswerFor(Owner));

        match.Answer(Other, 0, MatchingAnswer.NotApplicable(T0.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() => match.Answer(Owner, 0,
            MatchingAnswer.SelectedParticipant(Owner, T0.AddMinutes(4))));
    }

    [Fact]
    public void Every_active_participant_is_required_and_not_applicable_counts()
    {
        var match = NewMatch(ParticipantsQuestion());
        Assert.True(match.Join(Third, T0));
        Assert.True(match.Start(Owner, T0));

        match.Answer(Owner, 0, MatchingAnswer.NotApplicable(T0));
        match.Answer(Other, 0, MatchingAnswer.NotApplicable(T0));
        Assert.False(match.CanAdvance);
        Assert.Equal(0, match.CurrentSlot!.Slot);

        match.Answer(Third, 0, MatchingAnswer.NotApplicable(T0));
        Assert.True(match.IsOver);
        Assert.Equal(MatchState.Resolved, match.State);
        Assert.Null(match.CurrentSlot);
    }

    [Fact]
    public void Invalid_answer_shapes_for_the_current_slot_are_refused()
    {
        var participants = NewMatch(ParticipantsQuestion());
        Assert.True(participants.Start(Owner, T0));
        Assert.Throws<InvalidOperationException>(() => participants.Answer(Owner, 0,
            MatchingAnswer.SelectedChoice(0, T0)));
        Assert.Throws<InvalidOperationException>(() => participants.Answer(Owner, 0,
            MatchingAnswer.SelectedParticipant("stranger", T0)));
        Assert.Throws<InvalidOperationException>(() => participants.Answer("stranger", 0,
            MatchingAnswer.NotApplicable(T0)));

        var fixedMatch = NewMatch(FixedQuestion());
        Assert.True(fixedMatch.Start(Owner, T0));
        Assert.Throws<InvalidOperationException>(() => fixedMatch.Answer(Owner, 0,
            MatchingAnswer.SelectedParticipant(Owner, T0)));
    }

    [Fact]
    public void Departure_keeps_the_roster_and_options_but_excludes_the_participant_from_the_barrier()
    {
        var match = NewMatch(ParticipantsQuestion("mq-1"), ParticipantsQuestion("mq-2"));
        Assert.True(match.Join(Third, T0));
        Assert.True(match.Start(Owner, T0));
        var servedIds = match.CurrentSlot!.Options.Take(3).Select(x => x.ParticipantId).ToArray();

        Assert.True(match.Leave(Third, T0.AddMinutes(1)));
        Assert.Contains(Third, match.Participants);
        Assert.DoesNotContain(Third, match.ActiveParticipants);
        Assert.Equal(servedIds, match.CurrentSlot!.Options.Take(3).Select(x => x.ParticipantId));

        match.Answer(Owner, 0, MatchingAnswer.NotApplicable(T0.AddMinutes(2)));
        match.Answer(Other, 0, MatchingAnswer.NotApplicable(T0.AddMinutes(2)));
        Assert.Equal(1, match.CurrentSlot!.Slot);
        Assert.Throws<InvalidOperationException>(() => match.Answer(Third, 1,
            MatchingAnswer.NotApplicable(T0.AddMinutes(3))));
    }

    [Fact]
    public void Idle_expiry_is_per_slot_and_can_end_as_no_contest()
    {
        var match = NewMatch(ParticipantsQuestion());
        Assert.True(match.Start(Owner, T0));

        Assert.True(match.Advance(T0 + MatchingRules.IdleAfter));
        Assert.True(match.IsOver);
        Assert.Equal(MatchState.NoContest, match.State);
        Assert.Contains(Owner, match.InactiveParticipants);
        Assert.Contains(Other, match.InactiveParticipants);
    }

    [Fact]
    public void Owner_leaving_open_lobby_cancels_it_and_non_owner_leaving_frees_a_seat()
    {
        var match = MatchingMatch.Create("mm-1", "MCODE", Owner, 3, [ParticipantsQuestion()], T0);
        Assert.True(match.Join(Other, T0));
        Assert.True(match.Leave(Other, T0));
        Assert.DoesNotContain(Other, match.Participants);
        Assert.True(match.Leave(Owner, T0));
        Assert.Equal(MatchState.NoContest, match.State);
    }

    [Fact]
    public void Snapshot_json_round_trip_preserves_slots_answers_roster_current_slot_and_state()
    {
        var match = NewMatch(ParticipantsQuestion("mq-1"), FixedQuestion("mq-2", "Second"));
        Assert.True(match.Start(Owner, T0));
        match.Answer(Owner, 0, MatchingAnswer.SelectedParticipant(Other, T0.AddMinutes(1)));
        match.Answer(Other, 0, MatchingAnswer.NotApplicable(T0.AddMinutes(1)));
        Assert.True(match.Leave(Owner, T0.AddMinutes(2)));

        var snapshot = match.ToSnapshot();
        var restored = MatchingMatch.FromSnapshot(JsonSerializer.Deserialize<MatchingMatchSnapshot>(
            JsonSerializer.Serialize(snapshot))!);

        Assert.Equal(match.State, restored.State);
        Assert.Equal(match.Participants, restored.Participants);
        Assert.Equal(match.InactiveParticipants, restored.InactiveParticipants);
        Assert.Equal(match.CurrentSlot?.Slot, restored.CurrentSlot?.Slot);
        Assert.Equal(match.Slots[0].Prompt, restored.Slots[0].Prompt);
        Assert.Equal(match.Slots[0].Options, restored.Slots[0].Options);
        Assert.Equal(MatchingAnswerKind.NotApplicable, restored.ToSnapshot().Slots[0].Answers[Other].Kind);
    }

    [Fact]
    public void Public_surface_has_no_score_timer_or_presence_members()
    {
        var names = typeof(MatchingMatch).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => member.Name).ToHashSet();

        Assert.DoesNotContain("Score", names);
        Assert.DoesNotContain("WinnerId", names);
        Assert.DoesNotContain("IsDraw", names);
        Assert.DoesNotContain("Standings", names);
        Assert.DoesNotContain("QuestionTime", names);
        Assert.DoesNotContain("NetworkGrace", names);
    }
}
