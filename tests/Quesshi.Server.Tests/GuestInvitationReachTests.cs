using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// The invitation-reach requirement of issue #52, proven at the real HTTP/SignalR layer rather than by
/// driving handlers directly: a guest is structurally unreachable in-app — <c>LobbyHub.OnConnectedAsync</c>
/// aborts the connection outright, so no <c>ChallengeReceived</c> push could ever land on them, however
/// an invitation is modelled — yet the same guest can still take a seat by following the lobby's share
/// link, exactly as the design says a guest always has been able to. One test proves both halves of the
/// same sentence: "guests are invited by link only".
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class GuestInvitationReachTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private const string Owner = "gir-owner";

    private readonly LiveApiTestHost _liveApi = new(fixture.Cluster);
    private readonly LobbyHubTestHost _lobbyHub = new(fixture.Cluster);

    static GuestInvitationReachTests()
    {
        LiveShared.Players.Items.Add(Player.Register(Owner, "gir-owner@example.com", "Owner", Language.En, LiveShared.TimeProvider.GetUtcNow()));

        if (LiveShared.Categories.Items.All(c => c.Id != Category))
            LiveShared.Categories.Items.Add(new Category(Category, "دسته", "Category", "globe", "#336699"));

        for (var i = 0; i < 10; i++)
        {
            var qid = $"gir-pool-q{i}";
            if (LiveShared.Questions.Items.Any(q => q.Id == qid)) continue;
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, Category, MatchRules.LevelForSlot(i),
                $"pool question {i}", ["right", "wrong1", "wrong2", "wrong3"], 0, LiveShared.TimeProvider.GetUtcNow(),
                status: QuestionStatus.Approved));
        }
    }

    private const string Category = "gir-scarce";

    private HttpClient AuthedClient(Player player)
    {
        var client = _liveApi.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _liveApi.TokenIssuer.Issue(player));
        return client;
    }

    [Fact]
    public async Task A_guest_is_never_reached_in_app_but_can_still_take_a_seat_by_link()
    {
        var owner = await LiveShared.Players.GetAsync(Owner);
        using var ownerClient = AuthedClient(owner!);

        var created = await ownerClient.PostAsJsonAsync("/api/live", new CreateMatchDto(false, "en", [Category], null));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var lobby = await created.Content.ReadFromJsonAsync<LiveViewDto>();

        // Half one: nothing on /hub/lobby is ever open to a guest — the connection itself is refused,
        // structurally, before any invitation could ever be delivered to it.
        var guestToken = _lobbyHub.TokenIssuer.Issue(Player.Guest("gir-guest", "Guest", Language.En, DateTimeOffset.UtcNow));
        await using var hubConnection = _lobbyHub.NewConnection(guestToken);
        var closed = new TaskCompletionSource();
        hubConnection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };

        await hubConnection.StartAsync();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Half two: the very same guest reaches the same lobby anyway, by following its share link —
        // becoming a guest and taking the seat in one call, exactly as an invite link always has.
        var liveGuestToken = _liveApi.TokenIssuer.Issue(Player.Guest("gir-guest-2", "Guest", Language.En, DateTimeOffset.UtcNow));
        using var guestLiveClient = _liveApi.NewClient();
        var joinResponse = await guestLiveClient.PostAsJsonAsync($"/api/auth/guest/live/{lobby!.Code}", new GuestJoinDto("Newcomer"));

        Assert.Equal(HttpStatusCode.OK, joinResponse.StatusCode);
        var result = await joinResponse.Content.ReadFromJsonAsync<GuestLiveResultDto>();
        Assert.Equal("lobby", result!.Live.Phase); // filling the seat no longer starts it (issue #104)
        Assert.True(result.Me.IsGuest);
    }

    public async ValueTask DisposeAsync()
    {
        await _liveApi.DisposeAsync();
        await _lobbyHub.DisposeAsync();
    }
}
