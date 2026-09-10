using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// The kill switch, exercised through the real HTTP pipeline the same way
/// <see cref="LiveEndpointsGuestGateTests"/> does for the guest gate — the filter runs before any
/// handler, so this is the one place that can prove create and join actually refuse.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveKillSwitchEndpointTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly LiveApiTestHost _host = new(fixture.Cluster);
    private static int _n;
    private const string Category = "kse-category";

    static LiveKillSwitchEndpointTests()
    {
        if (LiveShared.Categories.Items.All(c => c.Id != Category))
            LiveShared.Categories.Items.Add(new Category(Category, "کلید", "Switch", "toggle", "#336699"));

        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"kse-pool-q{slot}";
            if (LiveShared.Questions.Items.Any(q => q.Id == qid)) continue;
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, Category, MatchRules.LevelForSlot(slot),
                $"kse pool question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, DateTimeOffset.UtcNow,
                status: QuestionStatus.Approved));
        }
    }

    private HttpClient AuthedClient(Player player)
    {
        var client = _host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.TokenIssuer.Issue(player));
        return client;
    }

    private Player NewPlayer()
    {
        var id = $"kse-{Interlocked.Increment(ref _n)}";
        var player = Player.Register(id, $"{id}@example.com", "Amir", Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(player);
        return player;
    }

    private ILiveSettingsGrain SettingsGrain => fixture.Cluster.GrainFactory.GetGrain<ILiveSettingsGrain>(0);

    [Fact]
    public async Task Toggling_the_switch_off_then_on_changes_whether_create_succeeds_in_one_run()
    {
        await SettingsGrain.SetEnabledAsync(true);
        using var client = AuthedClient(NewPlayer());

        var enabled = await client.PostAsJsonAsync("/api/live", new CreateMatchDto(false, "en", [Category], null));
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, enabled.StatusCode);

        await SettingsGrain.SetEnabledAsync(false);
        var disabled = await client.PostAsJsonAsync("/api/live", new CreateMatchDto(false, "en", [Category], null));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        var body = await disabled.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("live_disabled", body!["error"]);

        await SettingsGrain.SetEnabledAsync(true); // leave the shared cluster's switch as found
    }

    [Fact]
    public async Task While_off_joining_by_code_also_refuses()
    {
        await SettingsGrain.SetEnabledAsync(false);
        try
        {
            using var client = AuthedClient(NewPlayer());
            var response = await client.PostAsync("/api/live/join/SOMECODE", null);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            Assert.Equal("live_disabled", body!["error"]);
        }
        finally
        {
            await SettingsGrain.SetEnabledAsync(true);
        }
    }

    [Fact]
    public async Task While_off_a_duel_already_in_flight_still_answers_GET_and_can_still_be_cancelled()
    {
        await SettingsGrain.SetEnabledAsync(true);
        using var client = AuthedClient(NewPlayer());

        var created = await client.PostAsJsonAsync("/api/live", new CreateMatchDto(false, "en", [Category], null));
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, created.StatusCode);
        var view = await created.Content.ReadFromJsonAsync<LiveViewDto>();

        await SettingsGrain.SetEnabledAsync(false);
        try
        {
            var get = await client.GetAsync($"/api/live/{view!.Id}");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);

            var cancel = await client.DeleteAsync($"/api/live/{view.Id}");
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }
        finally
        {
            await SettingsGrain.SetEnabledAsync(true);
        }
    }

    /// <summary>
    /// Issue #104: <c>GET /api/live/by-code/{code}</c> carries no <c>RequiresLiveEnabled</c> marker on
    /// purpose — that marker belongs only to endpoints that would start a new live duel, and a lobby
    /// read is a read, so a lobby created while the switch was on must still be readable by code while
    /// it is off, the same way <see cref="While_off_a_duel_already_in_flight_still_answers_GET_and_can_still_be_cancelled"/>
    /// already proves for the participant-only GET.
    /// </summary>
    [Fact]
    public async Task While_off_reading_a_lobby_by_code_still_answers_GET()
    {
        await SettingsGrain.SetEnabledAsync(true);
        using var client = AuthedClient(NewPlayer());

        var created = await client.PostAsJsonAsync("/api/live", new CreateMatchDto(false, "en", [Category], null));
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, created.StatusCode);
        var view = await created.Content.ReadFromJsonAsync<LiveViewDto>();

        await SettingsGrain.SetEnabledAsync(false);
        try
        {
            var get = await client.GetAsync($"/api/live/by-code/{view!.Code}");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        }
        finally
        {
            await SettingsGrain.SetEnabledAsync(true);
        }
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
