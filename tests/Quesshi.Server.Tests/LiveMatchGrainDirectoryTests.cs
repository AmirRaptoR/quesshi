using System.Diagnostics;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// Covers the in-flight index <see cref="LiveMatchGrain"/> writes to on every phase change and
/// deletes when the duel ends — the acceptance criteria under "The in-flight index" in issue #16.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveMatchGrainDirectoryTests(LiveClusterFixture fixture)
{
    private const string Amir = "ldw-amir";
    private const string Sara = "ldw-sara";

    private ILiveMatchGrain NewGrain(out string id, out List<string> questionIds)
    {
        id = Guid.NewGuid().ToString("N");
        questionIds = SeedQuestions(id);
        return fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id);
    }

    private static List<string> SeedQuestions(string prefix)
    {
        if (LiveShared.Categories.Items.All(c => c.Id != "geography"))
            LiveShared.Categories.Items.Add(new Category("geography", "جغرافیا", "Geography", "globe", "#336699"));

        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, "geography", MatchRules.LevelForSlot(slot),
                $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, LiveShared.TimeProvider.GetUtcNow(),
                explanation: "because", status: QuestionStatus.Approved));
            ids.Add(qid);
        }
        return ids;
    }

    private static void Advance(TimeSpan by) => LiveShared.TimeProvider.Advance(by);

    private static async Task WaitUntilAsync(Func<bool> ready, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (ready()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition not reached in time.");
    }

    [Fact]
    public async Task Create_writes_a_lobby_row_to_the_directory()
    {
        var grain = NewGrain(out var id, out var questionIds);
        var view = await grain.CreateAsync("DIRTEST1", (int)Language.En, Amir, questionIds);

        Assert.True(LiveShared.Directory.Rows.TryGetValue(id, out var row));
        Assert.Equal([Amir], row!.Participants);
        Assert.Equal((int)LivePhase.Lobby, row.Phase);
        Assert.Equal(view.Id, row.MatchId);
    }

    [Fact]
    public async Task Join_rewrites_the_row_with_the_opponent()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("DIRTEST2", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);

        var row = LiveShared.Directory.Rows[id];
        Assert.Equal([Amir, Sara], row.Participants);
    }

    [Fact]
    public async Task Admin_ending_a_duel_removes_its_row()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("DIRTEST3", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);

        await grain.EndAsync("admin-ended");

        await WaitUntilAsync(() => !LiveShared.Directory.Rows.ContainsKey(id));
        Assert.False(LiveShared.Directory.Rows.ContainsKey(id));
    }

    [Fact]
    public async Task Cancelling_a_lobby_removes_its_row()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("DIRTEST4", (int)Language.En, Amir, questionIds);

        await grain.CancelAsync(Amir);

        await WaitUntilAsync(() => !LiveShared.Directory.Rows.ContainsKey(id));
        Assert.False(LiveShared.Directory.Rows.ContainsKey(id));
    }

    [Fact]
    public async Task A_lobby_nobody_joins_expires_and_its_row_is_removed()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("DIRTEST5", (int)Language.En, Amir, questionIds);
        Assert.True(LiveShared.Directory.Rows.ContainsKey(id));

        Advance(LiveRules.LobbyExpires + TimeSpan.FromSeconds(1));

        await WaitUntilAsync(() => !LiveShared.Directory.Rows.ContainsKey(id), timeoutMs: 10000);
        Assert.False(LiveShared.Directory.Rows.ContainsKey(id));
    }

    [Fact]
    public async Task A_directory_that_throws_does_not_stop_the_duel()
    {
        LiveShared.Directory.ThrowOnEveryCall = true;
        try
        {
            var grain = NewGrain(out _, out var questionIds);
            // Create must still succeed even though writing the index throws.
            var view = await grain.CreateAsync("DIRTEST6", (int)Language.En, Amir, questionIds);
            Assert.Equal((int)MatchState.AwaitingOpponent, view.State);
        }
        finally
        {
            LiveShared.Directory.ThrowOnEveryCall = false;
        }
    }
}
