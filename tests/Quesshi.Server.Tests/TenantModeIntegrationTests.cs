using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

[Collection(nameof(ClusterCollection))]
public sealed class TenantModeIntegrationTests(ClusterFixture fixture)
{
    [Fact]
    public async Task Voting_only_tenant_blocks_async_actions_but_keeps_voting_invites_and_shared_history()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var player = Player.Register($"mode-{suffix}", $"{suffix}@example.com", "Mode Tester", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(player);

        await using var host = new GameApiTestHost(fixture.Cluster, "voting");
        using var client = host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.TokenIssuer.Issue(player));

        var votingCreate = await client.PostAsJsonAsync("/api/voting/lobby",
            new CreateVotingLobbyDto("en", 10, [], [], 2, "voting"));
        Assert.Equal(HttpStatusCode.OK, votingCreate.StatusCode);
        var votingLobby = await votingCreate.Content.ReadFromJsonAsync<VotingViewDto>();
        Assert.NotNull(votingLobby);

        var asyncCreate = await client.PostAsJsonAsync("/api/matches", new CreateMatchDto());
        await AssertModeDisabled(asyncCreate);
        var asyncJoin = await client.PostAsync("/api/matches/join/NO-SUCH-CODE", null);
        await AssertModeDisabled(asyncJoin);

        var sharedList = await client.GetFromJsonAsync<List<MatchSummaryDto>>("/api/matches");
        Assert.Contains(sharedList!, match => match.Id == votingLobby!.Id && match.Mode == "voting");

        var asyncCode = $"ASY{suffix[..5]}".ToUpperInvariant();
        await Shared.Archive.SaveAsync(new ArchivedMatch($"async-{suffix}", asyncCode, Language.En,
            player.Id, null, null, false, [new ParticipantResult(player.Id, 0, 1, MatchOutcome.Draw)],
            MatchState.AwaitingOpponent, Shared.Clock.Now, null, []));

        var disabledInvite = await client.GetAsync($"/api/invite/{asyncCode}");
        await AssertModeDisabled(disabledInvite);
        var disabledGuestJoin = await client.PostAsJsonAsync($"/api/auth/guest/{asyncCode}", new GuestJoinDto("Guest"));
        await AssertModeDisabled(disabledGuestJoin);

        var votingInvite = await client.GetAsync($"/api/invite/{votingLobby!.Code}");
        Assert.Equal(HttpStatusCode.OK, votingInvite.StatusCode);
        var votingGuestJoin = await client.PostAsJsonAsync($"/api/auth/guest/{votingLobby.Code}", new GuestJoinDto("Voting Guest"));
        Assert.Equal(HttpStatusCode.OK, votingGuestJoin.StatusCode);
    }

    [Fact]
    public async Task Voting_only_tenant_blocks_live_creation_and_join_at_the_real_route_group()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var player = Player.Register($"live-mode-{suffix}", $"live-{suffix}@example.com", "Live Tester", Language.En, LiveShared.TimeProvider.GetUtcNow());

        await using var host = new LiveApiTestHost(fixture.Cluster, "voting");
        using var client = host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.TokenIssuer.Issue(player));

        var create = await client.PostAsJsonAsync("/api/live", new CreateMatchDto());
        await AssertModeDisabled(create);
        var join = await client.PostAsync("/api/live/join/NO-SUCH-CODE", null);
        await AssertModeDisabled(join);
    }

    private static async Task AssertModeDisabled(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("mode_disabled", await response.Content.ReadAsStringAsync());
    }
}
