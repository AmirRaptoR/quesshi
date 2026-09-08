using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Quesshi.Domain;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// <c>LiveHub</c> over a real <see cref="HubConnection"/> against an in-memory <c>TestServer</c> —
/// #13's own review note recorded that no such pattern existed in this repo yet. Grain-originated
/// pushes (<c>RoundStarted</c> etc.) are not covered here: <see cref="LiveTestSilo"/>'s silo and
/// <see cref="LiveApiTestHost"/>'s web host are two separate hosts with two separate
/// <c>ILiveNotifier</c> instances (production shares one composition root; this harness's Redis-free
/// split does not), so a grain's notification never reaches a hub client in this topology. What is
/// covered is everything the hub itself is responsible for: authenticating the connection, resolving
/// the caller's own catch-up view (proving the #13 DTO enrichment reaches the wire), and refusing a
/// non-participant.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveHubTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly LiveApiTestHost _host = new(fixture.Cluster);
    private static int _n;

    /// <summary>This class's own category+pool — a static cctor in another test class is not
    /// guaranteed to have run yet when only this class's tests are selected.</summary>
    private const string LiveScarceCategory = "lh-scarce";

    static LiveHubTests()
    {
        if (LiveShared.Categories.Items.All(c => c.Id != LiveScarceCategory))
            LiveShared.Categories.Items.Add(new Category(LiveScarceCategory, "کمیاب", "Scarce", "globe", "#336699"));

        for (var i = 0; i < MatchRules.QuestionsPerMatch; i++)
        {
            var qid = $"lh-pool-q{i}";
            if (LiveShared.Questions.Items.Any(q => q.Id == qid)) continue;
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, LiveScarceCategory, MatchRules.LevelForSlot(i),
                $"pool question {i}", ["right", "wrong1", "wrong2", "wrong3"], 0, DateTimeOffset.UtcNow,
                status: QuestionStatus.Approved));
        }
    }

    private HttpClient AuthedClient(Player player)
    {
        var client = _host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.TokenIssuer.Issue(player));
        return client;
    }

    private async Task<(Player Challenger, Player Opponent, LiveViewDto View)> NewDuelAsync()
    {
        var n = Interlocked.Increment(ref _n);
        var challenger = Player.Register($"lh-challenger{n}", $"lh-c{n}@example.com", "Challenger", Language.En, DateTimeOffset.UtcNow);
        var opponent = Player.Register($"lh-opponent{n}", $"lh-o{n}@example.com", "Opponent", Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(challenger);
        LiveShared.Players.Items.Add(opponent);

        using var challengerClient = AuthedClient(challenger);
        var createResponse = await challengerClient.PostAsJsonAsync("/api/live", new CreateMatchDto(false, "en", [LiveScarceCategory], null));
        var created = (await createResponse.Content.ReadFromJsonAsync<LiveViewDto>())!;

        using var opponentClient = AuthedClient(opponent);
        var joinResponse = await opponentClient.PostAsync($"/api/live/join/{created.Code}", null);
        var joined = (await joinResponse.Content.ReadFromJsonAsync<LiveViewDto>())!;

        return (challenger, opponent, joined);
    }

    [Fact]
    public async Task Join_returns_the_enriched_catchup_view_for_a_participant()
    {
        var (challenger, _, view) = await NewDuelAsync();

        await using var connection = _host.NewHubConnection(_host.TokenIssuer.Issue(challenger));
        await connection.StartAsync();

        var result = await connection.InvokeAsync<LiveViewDto>("Join", view.Id);

        Assert.Equal(view.Id, result.Id);
        Assert.Equal("Challenger", result.Participants[0].Name);
        Assert.Equal("Opponent", result.Participants[1].Name);
        Assert.NotEqual("", result.Participants[0].Avatar);
    }

    [Fact]
    public async Task Join_refuses_a_non_participant()
    {
        var (_, _, view) = await NewDuelAsync();
        var n = Interlocked.Increment(ref _n);
        var stranger = Player.Register($"lh-stranger{n}", $"lh-s{n}@example.com", "Stranger", Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(stranger);

        await using var connection = _host.NewHubConnection(_host.TokenIssuer.Issue(stranger));
        await connection.StartAsync();

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<LiveViewDto>("Join", view.Id));
    }

    [Fact]
    public async Task Leave_does_not_throw_for_a_connection_that_has_joined()
    {
        var (challenger, _, view) = await NewDuelAsync();

        await using var connection = _host.NewHubConnection(_host.TokenIssuer.Issue(challenger));
        await connection.StartAsync();
        await connection.InvokeAsync<LiveViewDto>("Join", view.Id);

        await connection.InvokeAsync("Leave", view.Id);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
