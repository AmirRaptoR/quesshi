using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// <see cref="LiveHub.Rematch"/>'s own job per the issue's technical notes: the guest gate, refusing
/// a non-participant or a duel with no opponent, and — for two real accounts — that the hub's return
/// value reflects the grain's idempotent lobby creation (issue #51: no more readiness handshake, so
/// both participants' presses land on the very same lobby id). <see cref="LiveHubTests"/>' own class
/// doc explains why a grain-originated push is never asserted here: this silo and this host are two
/// separate <c>ILiveNotifier</c> instances, so only the hub call's own return value is provable.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveRematchHubTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly LiveApiTestHost _host = new(fixture.Cluster);
    private static int _n;
    private const string Category = "rmh-category";

    static LiveRematchHubTests()
    {
        if (LiveShared.Categories.Items.All(c => c.Id != Category))
            LiveShared.Categories.Items.Add(new Category(Category, "دسته", "Category", "globe", "#336699"));

        for (var i = 0; i < 100; i++)
        {
            var qid = $"rmh-pool-q{i}";
            if (LiveShared.Questions.Items.Any(q => q.Id == qid)) continue;
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, Category, MatchRules.LevelForSlot(i, 100),
                $"rmh pool question {i}", ["right", "wrong1", "wrong2", "wrong3"], 0, DateTimeOffset.UtcNow,
                status: QuestionStatus.Approved));
        }
    }

    private static List<string> QuestionIds(int count) => [.. LiveShared.Questions.Items.Where(q => q.CategoryId == Category).Take(count).Select(q => q.Id)];

    private HubConnection Connect(Player player) => _host.NewHubConnection(_host.TokenIssuer.Issue(player));

    private async Task<(Player Challenger, Player Opponent, string MatchId)> NewFinishedDuelAsync(bool opponentIsGuest = false)
    {
        var n = Interlocked.Increment(ref _n);
        var challenger = Player.Register($"rmh-c{n}", $"rmh-c{n}@example.com", "Challenger", Language.En, DateTimeOffset.UtcNow);
        var opponent = opponentIsGuest
            ? Player.Guest($"rmh-o{n}", "Guest", Language.En, DateTimeOffset.UtcNow)
            : Player.Register($"rmh-o{n}", $"rmh-o{n}@example.com", "Opponent", Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(challenger);
        LiveShared.Players.Items.Add(opponent);

        var matchId = Guid.NewGuid().ToString("N");
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(matchId);
        await grain.CreateAsync($"RMH{n}", (int)Language.En, challenger.Id, QuestionIds(10));
        await grain.JoinAsync(opponent.Id);
        await grain.EndAsync("test");

        return (challenger, opponent, matchId);
    }

    [Fact]
    public async Task A_guest_pressing_rematch_is_refused()
    {
        var (_, opponent, matchId) = await NewFinishedDuelAsync();
        var n = Interlocked.Increment(ref _n);
        var guestChallenger = Player.Guest($"rmh-guest{n}", "Guest", Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(guestChallenger);
        // Not a participant of the duel above, but the guest gate must refuse before that is even checked.
        _ = opponent;

        await using var connection = Connect(guestChallenger);
        await connection.StartAsync();

        var outcome = await connection.InvokeAsync<RematchOutcomeDto>("Rematch", matchId);

        Assert.Equal("refused", outcome.Status);
    }

    [Fact]
    public async Task Pressing_rematch_against_a_guest_opponent_is_refused()
    {
        var (challenger, _, matchId) = await NewFinishedDuelAsync(opponentIsGuest: true);

        await using var connection = Connect(challenger);
        await connection.StartAsync();

        var outcome = await connection.InvokeAsync<RematchOutcomeDto>("Rematch", matchId);

        Assert.Equal("refused", outcome.Status);
    }

    [Fact]
    public async Task A_non_participant_pressing_rematch_is_refused()
    {
        var (_, _, matchId) = await NewFinishedDuelAsync();
        var n = Interlocked.Increment(ref _n);
        var stranger = Player.Register($"rmh-s{n}", $"rmh-s{n}@example.com", "Stranger", Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(stranger);

        await using var connection = Connect(stranger);
        await connection.StartAsync();

        var outcome = await connection.InvokeAsync<RematchOutcomeDto>("Rematch", matchId);

        Assert.Equal("refused", outcome.Status);
    }

    [Fact]
    public async Task A_duel_with_no_opponent_is_refused_through_the_hub()
    {
        var n = Interlocked.Increment(ref _n);
        var challenger = Player.Register($"rmh-lone{n}", $"rmh-lone{n}@example.com", "Lone", Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(challenger);

        var matchId = Guid.NewGuid().ToString("N");
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(matchId);
        await grain.CreateAsync($"RMHL{n}", (int)Language.En, challenger.Id, QuestionIds(10));
        await grain.CancelAsync(challenger.Id);

        await using var connection = Connect(challenger);
        await connection.StartAsync();

        var outcome = await connection.InvokeAsync<RematchOutcomeDto>("Rematch", matchId);

        Assert.Equal("refused", outcome.Status);
    }

    [Fact]
    public async Task Two_real_accounts_pressing_rematch_both_land_on_the_same_lobby_through_the_hub()
    {
        var (challenger, opponent, matchId) = await NewFinishedDuelAsync();

        await using var challengerConn = Connect(challenger);
        await challengerConn.StartAsync();
        await using var opponentConn = Connect(opponent);
        await opponentConn.StartAsync();

        var first = await challengerConn.InvokeAsync<RematchOutcomeDto>("Rematch", matchId);
        Assert.Equal("created", first.Status);
        Assert.NotNull(first.NewMatchId);

        var second = await opponentConn.InvokeAsync<RematchOutcomeDto>("Rematch", matchId);
        Assert.Equal("created", second.Status);
        Assert.Equal(first.NewMatchId, second.NewMatchId);
    }

    [Fact]
    public async Task LiveViewDto_carries_each_participants_guest_status_on_join()
    {
        var n = Interlocked.Increment(ref _n);
        var challenger = Player.Register($"rmh-g1-{n}", $"rmh-g1-{n}@example.com", "Challenger", Language.En, DateTimeOffset.UtcNow);
        var guestOpponent = Player.Guest($"rmh-g2-{n}", "Guest", Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(challenger);
        LiveShared.Players.Items.Add(guestOpponent);

        var matchId = Guid.NewGuid().ToString("N");
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(matchId);
        await grain.CreateAsync($"RMHV{n}", (int)Language.En, challenger.Id, QuestionIds(10));
        await grain.JoinAsync(guestOpponent.Id);

        await using var connection = Connect(challenger);
        await connection.StartAsync();

        var view = await connection.InvokeAsync<LiveViewDto>("Join", matchId);

        Assert.False(view.Participants[0].IsGuest);
        Assert.True(view.Participants[1].IsGuest);
    }

    [Fact]
    public async Task LiveViewDto_carries_each_participants_guest_status_on_GET()
    {
        var n = Interlocked.Increment(ref _n);
        var challenger = Player.Register($"rmh-r1-{n}", $"rmh-r1-{n}@example.com", "Challenger", Language.En, DateTimeOffset.UtcNow);
        var guestOpponent = Player.Guest($"rmh-r2-{n}", "Guest", Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(challenger);
        LiveShared.Players.Items.Add(guestOpponent);

        var matchId = Guid.NewGuid().ToString("N");
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(matchId);
        await grain.CreateAsync($"RMHR{n}", (int)Language.En, challenger.Id, QuestionIds(10));
        await grain.JoinAsync(guestOpponent.Id);

        using var client = _host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.TokenIssuer.Issue(challenger));

        var view = await client.GetFromJsonAsync<LiveViewDto>($"/api/live/{matchId}");

        Assert.False(view!.Participants[0].IsGuest);
        Assert.True(view.Participants[1].IsGuest);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
