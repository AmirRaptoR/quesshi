using System.Text.Json;
using Quesshi.Application.Ports;
using Orleans.Core.Internal;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

[Collection(nameof(ClusterCollection))]
public sealed class VotingMatchGrainTests(ClusterFixture fixture)
{
    private const string Owner = "voting-owner";
    private const string Other = "voting-other";

    [Fact]
    public async Task Lobby_start_answer_archive_and_reactivation_preserve_voting_state()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var id = $"voting-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id);

        var created = await grain.CreateAsync($"M{prefix[..5]}", Owner, (int)Language.En, 10, [category], 2);
        Assert.Equal((int)MatchState.AwaitingOpponent, created.State);
        Assert.True((VotingJoinResult)await grain.JoinAsync(Other) is VotingJoinResult.Joined);
        Assert.True(await grain.StartAsync(Owner));

        var started = await grain.GetAsync(Owner);
        Assert.NotNull(started);
        Assert.Equal(0, started!.CurrentSlotIndex);
        Assert.Equal(2, started.CurrentSlot!.Options.Count(o => o.ParticipantId is not null));
        Assert.Null(started.Results);

        var answered = await grain.AnswerAsync(Owner, 0, (int)VotingAnswerKind.NoParticipant, null, null);
        Assert.Equal(0, answered.CurrentSlotIndex);
        Assert.Single(answered.CurrentSlot!.AnsweredParticipantIds);
        Assert.NotNull(answered.OwnAnswer);

        await fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id)
            .AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        var restored = await fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id).GetAsync(Owner);
        Assert.NotNull(restored);
        Assert.Equal(answered.CurrentSlotIndex, restored!.CurrentSlotIndex);
        Assert.Equal(answered.CurrentSlot!.Options, restored.CurrentSlot!.Options);
        Assert.Equal(answered.OwnAnswer, restored.OwnAnswer);
        Assert.Equal(GameMode.Voting, Shared.Archive.Items.Single(x => x.Id == id).Mode);
        Assert.False(Shared.Archive.Items.Single(x => x.Id == id).IsLive);

        await grain.AnswerAsync(Other, 0, (int)VotingAnswerKind.NoParticipant, null, null);
        var afterBarrier = await grain.GetAsync(Other);
        Assert.Equal(1, afterBarrier!.CurrentSlotIndex);
        Assert.Null(afterBarrier.LastClosedSlot);
        Assert.Null(afterBarrier.Results);
        Assert.Empty(afterBarrier.ClosedSlots!);
    }

    [Fact]
    public async Task Stranger_is_redacted_while_lobby_is_open_but_not_after_start()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>($"voting-{prefix}");
        await grain.CreateAsync($"M{prefix[..5]}", Owner, (int)Language.En, 10, [category], 2);

        Assert.NotNull(await grain.GetAsync("not-seated"));
        await grain.JoinAsync(Other);
        Assert.True(await grain.StartAsync(Owner));
        Assert.Null(await grain.GetAsync("not-seated"));
    }

    [Fact]
    public async Task Notifier_failure_does_not_rollback_a_persisted_answer()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var id = $"voting-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id);
        await grain.CreateAsync($"M{prefix[..5]}", Owner, (int)Language.En, 10, [category], 2);
        await grain.JoinAsync(Other);
        Assert.True(await grain.StartAsync(Owner));

        Shared.VotingNotifier.ThrowOnEveryCall = true;
        try
        {
            await grain.AnswerAsync(Owner, 0, (int)VotingAnswerKind.NoParticipant, null, null);
        }
        finally
        {
            Shared.VotingNotifier.ThrowOnEveryCall = false;
        }

        Assert.NotNull((await grain.GetAsync(Owner))!.OwnAnswer);
    }

    [Fact]
    public async Task Archive_failure_is_replayed_from_the_durable_marker_after_reactivation()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var id = $"voting-archive-retry-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id);
        await grain.CreateAsync($"M{prefix[..5]}", Owner, (int)Language.En, 10, [category], 2);
        await grain.JoinAsync(Other);
        Assert.True(await grain.StartAsync(Owner));

        Shared.Archive.FailingWritesRemaining = 1;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.AnswerAsync(
                Owner, 0, (int)VotingAnswerKind.NoParticipant, null, null));
        }
        finally
        {
            Shared.Archive.FailingWritesRemaining = 0;
        }

        // The hot state was committed before the simulated archive outage. A fresh activation must
        // notice the durable outbox marker and make the archive converge without another answer.
        await fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id)
            .AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        var restored = await fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id).GetAsync(Owner);
        Assert.NotNull(restored!.OwnAnswer);
        var archived = Assert.Single(Shared.Archive.Items, item => item.Id == id);
        Assert.Equal(MatchState.InProgress, archived.State);
        Assert.Equal(10, archived.QuestionIds.Count);
    }

    [Fact]
    public async Task Served_question_counts_are_persisted_once_per_slot_and_survive_reactivation()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var id = $"voting-served-count-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id);
        await grain.CreateAsync($"M{prefix[..5]}", Owner, (int)Language.En, 10, [category], 2);
        await grain.JoinAsync(Other);
        Assert.True(await grain.StartAsync(Owner));

        var first = await grain.GetAsync(Owner);
        var firstQuestionId = first!.CurrentSlot!.QuestionId;
        Assert.Equal(1, Shared.VotingQuestions.Items.Single(q => q.Id == firstQuestionId).TimesServed);
        Assert.Equal(1, Shared.VotingQuestions.RecordServedQuestionCounts[firstQuestionId]);

        await fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id)
            .AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);
        var restored = await grain.GetAsync(Owner);
        Assert.Equal(firstQuestionId, restored!.CurrentSlot!.QuestionId);
        Assert.Equal(1, Shared.VotingQuestions.RecordServedQuestionCounts[firstQuestionId]);

        await grain.AnswerAsync(Owner, 0, (int)VotingAnswerKind.NoParticipant, null, null);
        await grain.AnswerAsync(Other, 0, (int)VotingAnswerKind.NoParticipant, null, null);
        var next = await grain.GetAsync(Owner);
        var secondQuestionId = next!.CurrentSlot!.QuestionId;
        Assert.NotEqual(firstQuestionId, secondQuestionId);
        Assert.Equal(1, Shared.VotingQuestions.Items.Single(q => q.Id == secondQuestionId).TimesServed);
        Assert.Equal(1, Shared.VotingQuestions.RecordServedQuestionCounts[secondQuestionId]);

        await fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id)
            .AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);
        Assert.Equal(1, Shared.VotingQuestions.RecordServedQuestionCounts[firstQuestionId]);
        Assert.Equal(1, Shared.VotingQuestions.RecordServedQuestionCounts[secondQuestionId]);
    }

    [Fact]
    public async Task A_counter_failure_replays_the_same_serve_token_after_reactivation()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var id = $"voting-counter-retry-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id);
        await grain.CreateAsync($"M{prefix[..5]}", Owner, (int)Language.En, 10, [category], 2);
        await grain.JoinAsync(Other);

        Shared.VotingQuestions.FailRecordServedCalls = 1;
        Assert.True(await grain.StartAsync(Owner));
        var firstQuestionId = (await grain.GetAsync(Owner))!.CurrentSlot!.QuestionId;
        Assert.Equal(0, Shared.VotingQuestions.Items.Single(q => q.Id == firstQuestionId).TimesServed);

        await fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id)
            .AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        Assert.NotNull(await grain.GetAsync(Owner));
        Assert.Equal(1, Shared.VotingQuestions.Items.Single(q => q.Id == firstQuestionId).TimesServed);
        Assert.Equal(1, Shared.VotingQuestions.RecordServedQuestionCounts[firstQuestionId]);
    }

    [Fact]
    public async Task Concurrent_matches_increment_the_same_question_once_per_match_slot()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var ownerA = $"voting-concurrent-owner-a-{prefix}";
        var otherA = $"voting-concurrent-other-a-{prefix}";
        var ownerB = $"voting-concurrent-owner-b-{prefix}";
        var otherB = $"voting-concurrent-other-b-{prefix}";
        var first = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>($"voting-concurrent-a-{prefix}");
        var second = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>($"voting-concurrent-b-{prefix}");
        await first.CreateAsync($"A{prefix[..5]}", ownerA, (int)Language.En, 10, [category], 2);
        await first.JoinAsync(otherA);
        await second.CreateAsync($"B{prefix[..5]}", ownerB, (int)Language.En, 10, [category], 2);
        await second.JoinAsync(otherB);

        await Task.WhenAll(first.StartAsync(ownerA), second.StartAsync(ownerB));

        var firstQuestionId = (await first.GetAsync(ownerA))!.CurrentSlot!.QuestionId;
        var secondQuestionId = (await second.GetAsync(ownerB))!.CurrentSlot!.QuestionId;
        Assert.Equal(firstQuestionId, secondQuestionId);
        Assert.Equal(2, Shared.VotingQuestions.Items.Single(q => q.Id == firstQuestionId).TimesServed);
        Assert.Equal(2, Shared.VotingQuestions.RecordServedQuestionCounts[firstQuestionId]);
    }

    [Fact]
    public async Task Leave_that_closes_the_barrier_records_the_newly_served_slot_once()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var third = $"voting-leave-third-{prefix}";
        var departed = $"voting-leave-departed-{prefix}";
        var id = $"voting-leave-advance-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id);
        await grain.CreateAsync($"M{prefix[..5]}", Owner, (int)Language.En, 10, [category], 4);
        await grain.JoinAsync(Other);
        await grain.JoinAsync(third);
        await grain.JoinAsync(departed);
        Assert.True(await grain.StartAsync(Owner));

        await grain.AnswerAsync(Owner, 0, (int)VotingAnswerKind.NoParticipant, null, null);
        await grain.AnswerAsync(Other, 0, (int)VotingAnswerKind.NoParticipant, null, null);
        await grain.AnswerAsync(third, 0, (int)VotingAnswerKind.NoParticipant, null, null);
        Assert.True(await grain.LeaveAsync(departed));

        var view = await grain.GetAsync(Owner);
        var nextQuestionId = view!.CurrentSlot!.QuestionId;
        Assert.Equal(1, view.CurrentSlotIndex);
        Assert.Equal(1, Shared.VotingQuestions.Items.Single(q => q.Id == nextQuestionId).TimesServed);
        Assert.Equal(1, Shared.VotingQuestions.RecordServedQuestionCounts[nextQuestionId]);
    }

    [Fact]
    public async Task Reminder_expires_idle_match_and_is_safe_to_run_again()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var id = $"voting-reminder-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id);
        await grain.CreateAsync($"M{prefix[..5]}", Owner, (int)Language.En, 10, [category], 2);
        await grain.JoinAsync(Other);
        Assert.True(await grain.StartAsync(Owner));

        var before = Shared.Clock.Now;
        try
        {
            Shared.Clock.Advance(VotingRules.IdleAfter + TimeSpan.FromMinutes(1));
            await fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id)
                .AsReference<IRemindable>().ReceiveReminder("voting-idle", default);

            var expired = await grain.GetAsync(Owner);
            Assert.Equal((int)MatchState.NoContest, expired!.State);
            Assert.All(expired.Participants, participant => Assert.False(participant.Active));

            // A stale reminder after the terminal write must clean itself up and never mutate again.
            await fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id)
                .AsReference<IRemindable>().ReceiveReminder("voting-idle", default);
            Assert.Equal((int)MatchState.NoContest, (await grain.GetAsync(Owner))!.State);
        }
        finally
        {
            Shared.Clock.Now = before;
        }
    }

    [Fact]
    public async Task Barrier_push_redacts_answers_until_the_match_ends()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var id = $"voting-push-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id);
        await grain.CreateAsync($"M{prefix[..5]}", Owner, (int)Language.En, 10, [category], 2);
        await grain.JoinAsync(Other);
        Assert.True(await grain.StartAsync(Owner));

        await grain.AnswerAsync(Owner, 0, (int)VotingAnswerKind.NoParticipant, null, null);
        await grain.AnswerAsync(Other, 0, (int)VotingAnswerKind.NoParticipant, null, null);

        var push = Shared.VotingNotifier.EventsFor(id).Last(e => e.Kind == "SlotClosed");
        var payload = Assert.IsType<VotingSlotClosedPush>(push.Payload);
        Assert.Equal(0, payload.Slot);
        Assert.Empty(payload.Answers);
        var view = await grain.GetAsync(Owner);
        Assert.Equal(1, view!.CurrentSlotIndex);
        Assert.Null(view.LastClosedSlot);
    }

    [Fact]
    public async Task Leaving_before_answering_keeps_the_departed_missing_from_closed_distribution()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = $"voting-leave-owner-{prefix}";
        var other = $"voting-leave-other-{prefix}";
        var departed = $"voting-leave-departed-{prefix}";
        var id = $"voting-results-leave-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id);

        await grain.CreateAsync($"M{prefix[..5]}", owner, (int)Language.En, 10, [category], 3);
        await grain.JoinAsync(other);
        await grain.JoinAsync(departed);
        Assert.True(await grain.StartAsync(owner));

        Assert.True(await grain.LeaveAsync(departed));
        await grain.AnswerAsync(owner, 0, (int)VotingAnswerKind.SelectedParticipant, other, null);
        var afterBarrier = await grain.AnswerAsync(other, 0, (int)VotingAnswerKind.SelectedParticipant, other, null);

        Assert.Null(afterBarrier.Results);
        Assert.Null(afterBarrier.LastClosedSlot);
    }

    [Fact]
    public async Task Idle_expiry_keeps_the_expired_missing_from_closed_distribution()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = $"voting-idle-owner-{prefix}";
        var other = $"voting-idle-other-{prefix}";
        var expired = $"voting-idle-expired-{prefix}";
        var id = $"voting-results-idle-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id);

        await grain.CreateAsync($"M{prefix[..5]}", owner, (int)Language.En, 10, [category], 3);
        await grain.JoinAsync(other);
        await grain.JoinAsync(expired);
        Assert.True(await grain.StartAsync(owner));
        await grain.AnswerAsync(owner, 0, (int)VotingAnswerKind.SelectedParticipant, other, null);
        await grain.AnswerAsync(other, 0, (int)VotingAnswerKind.SelectedParticipant, other, null);

        var before = Shared.Clock.Now;
        try
        {
            Shared.Clock.Advance(VotingRules.IdleAfter + TimeSpan.FromMinutes(1));
            await fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id)
                .AsReference<IRemindable>().ReceiveReminder("voting-idle", default);

            var view = await grain.GetAsync(owner);
            Assert.Null(view!.Results);
            Assert.Null(view.LastClosedSlot);
            Assert.False(view.Participants.Single(participant => participant.Id == expired).Active);
        }
        finally
        {
            Shared.Clock.Now = before;
        }
    }

    [Fact]
    public async Task Voting_completion_does_not_touch_leaderboard_or_player_stats()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"voting-owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"voting-other-{prefix}", $"other-{prefix}@example.com", "Other", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);
        var ownerStats = owner.Stats;
        var otherStats = other.Stats;
        var id = $"voting-side-effects-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IVotingMatchGrain>(id);
        await grain.CreateAsync($"M{prefix[..5]}", owner.Id, (int)Language.En, 10, [category], 2);
        await grain.JoinAsync(other.Id);
        Assert.True(await grain.StartAsync(owner.Id));
        await grain.LeaveAsync(other.Id);

        Assert.DoesNotContain(Shared.Leaderboard.Scores, kv => kv.Key == owner.Id || kv.Key == other.Id);
        Assert.Equal(ownerStats, (await Shared.Players.GetAsync(owner.Id))!.Stats);
        Assert.Equal(otherStats, (await Shared.Players.GetAsync(other.Id))!.Stats);
    }

    private static string Seed(string prefix)
    {
        var categoryId = $"m-{prefix}";
        Shared.VotingCategories.Items.Add(new Category(categoryId, "آزمون", "Test", "x", "#000"));
        for (var i = 0; i < 10; i++)
        {
            Shared.VotingQuestions.Items.Add(VotingQuestion.Create($"mq-{prefix}-{i}", Language.En,
                categoryId, $"Prompt {i}", VotingAnswerSource.Participants, null, Shared.Clock.Now,
                status: QuestionStatus.Approved));
        }
        return categoryId;
    }
}
