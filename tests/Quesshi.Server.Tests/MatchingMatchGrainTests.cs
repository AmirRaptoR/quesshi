using System.Text.Json;
using Orleans.Core.Internal;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

[Collection(nameof(ClusterCollection))]
public sealed class MatchingMatchGrainTests(ClusterFixture fixture)
{
    private const string Owner = "matching-owner";
    private const string Other = "matching-other";

    [Fact]
    public async Task Lobby_start_answer_archive_and_reactivation_preserve_matching_state()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var id = $"matching-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>(id);

        var created = await grain.CreateAsync($"M{prefix[..5]}", Owner, (int)Language.En, 10, [category], 2);
        Assert.Equal((int)MatchState.AwaitingOpponent, created.State);
        Assert.True((MatchingJoinResult)await grain.JoinAsync(Other) is MatchingJoinResult.Joined);
        Assert.True(await grain.StartAsync(Owner));

        var started = await grain.GetAsync(Owner);
        Assert.NotNull(started);
        Assert.Equal(0, started!.CurrentSlotIndex);
        Assert.Equal(2, started.CurrentSlot!.Options.Count(o => o.ParticipantId is not null));

        var answered = await grain.AnswerAsync(Owner, 0, (int)MatchingAnswerKind.NotApplicable, null, null);
        Assert.Equal(0, answered.CurrentSlotIndex);
        Assert.Single(answered.CurrentSlot!.AnsweredParticipantIds);
        Assert.NotNull(answered.OwnAnswer);

        await fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>(id)
            .AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        var restored = await fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>(id).GetAsync(Owner);
        Assert.NotNull(restored);
        Assert.Equal(answered.CurrentSlotIndex, restored!.CurrentSlotIndex);
        Assert.Equal(answered.CurrentSlot!.Options, restored.CurrentSlot!.Options);
        Assert.Equal(answered.OwnAnswer, restored.OwnAnswer);
        Assert.Equal(GameMode.Matching, Shared.Archive.Items.Single(x => x.Id == id).Mode);
        Assert.False(Shared.Archive.Items.Single(x => x.Id == id).IsLive);

        await grain.AnswerAsync(Other, 0, (int)MatchingAnswerKind.NotApplicable, null, null);
        var afterBarrier = await grain.GetAsync(Other);
        Assert.Equal(1, afterBarrier!.CurrentSlotIndex);
        Assert.Equal(0, afterBarrier.LastClosedSlot!.Slot);
    }

    [Fact]
    public async Task Stranger_is_redacted_while_lobby_is_open_but_not_after_start()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var grain = fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>($"matching-{prefix}");
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
        var id = $"matching-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>(id);
        await grain.CreateAsync($"M{prefix[..5]}", Owner, (int)Language.En, 10, [category], 2);
        await grain.JoinAsync(Other);
        Assert.True(await grain.StartAsync(Owner));

        Shared.MatchingNotifier.ThrowOnEveryCall = true;
        try
        {
            await grain.AnswerAsync(Owner, 0, (int)MatchingAnswerKind.NotApplicable, null, null);
        }
        finally
        {
            Shared.MatchingNotifier.ThrowOnEveryCall = false;
        }

        Assert.NotNull((await grain.GetAsync(Owner))!.OwnAnswer);
    }

    private static string Seed(string prefix)
    {
        var categoryId = $"m-{prefix}";
        Shared.MatchingCategories.Items.Add(new MatchingCategory(categoryId, "آزمون", "Test", "x", "#000"));
        for (var i = 0; i < 10; i++)
        {
            Shared.MatchingQuestions.Items.Add(MatchingQuestion.Create($"mq-{prefix}-{i}", Language.En,
                categoryId, $"Prompt {i}", MatchingAnswerSource.Participants, null, Shared.Clock.Now,
                status: QuestionStatus.Approved));
        }
        return categoryId;
    }
}
