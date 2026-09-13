using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

[Collection(nameof(ClusterCollection))]
public sealed class MatchingEndpointTests(ClusterFixture fixture)
{
    [Fact]
    public async Task Create_join_read_and_answer_redacts_pending_opponent_answers()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"other-{prefix}", $"other-{prefix}@example.com", "Other", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);

        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);
        using var otherClient = Authenticated(host, other);

        var create = await ownerClient.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));
        create.EnsureSuccessStatusCode();
        var lobby = await create.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(lobby);

        var joined = await otherClient.PostAsync($"/api/matching/join/{lobby!.Code}", null);
        joined.EnsureSuccessStatusCode();
        (await ownerClient.PostAsync($"/api/matching/{lobby.Id}/start", null)).EnsureSuccessStatusCode();

        var submitted = await ownerClient.PostAsJsonAsync($"/api/matching/{lobby.Id}/answer",
            new SubmitMatchingAnswerDto(0, "na"));
        submitted.EnsureSuccessStatusCode();
        var ownerView = await submitted.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(ownerView!.OwnAnswer);
        Assert.Empty(ownerView.CurrentSlot!.Answers);
        Assert.Contains(ownerView.CurrentSlot.AnsweredParticipantIds, id => id == owner.Id);

        var otherView = await otherClient.GetFromJsonAsync<MatchingViewDto>($"/api/matching/{lobby.Id}");
        Assert.Empty(otherView!.CurrentSlot!.Answers);
        Assert.Contains(otherView.CurrentSlot.AnsweredParticipantIds, id => id == owner.Id);

        var closed = await otherClient.PostAsJsonAsync($"/api/matching/{lobby.Id}/answer",
            new SubmitMatchingAnswerDto(0, "na"));
        closed.EnsureSuccessStatusCode();
        var closedView = await closed.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.Equal(1, closedView!.CurrentSlotIndex);
        Assert.Equal(0, closedView.LastClosedSlot!.Slot);
        Assert.Equal(2, closedView.LastClosedSlot.Answers.Count);
    }

    [Fact]
    public async Task Guest_can_join_read_and_answer_but_cannot_start()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var guest = Player.Guest($"guest-{prefix}", "Guest", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(guest);

        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);
        using var guestClient = Authenticated(host, guest);
        var response = await ownerClient.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));
        var lobby = await response.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(lobby);

        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await guestClient.PostAsync($"/api/matching/join/{lobby!.Code}", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await guestClient.PostAsync($"/api/matching/{lobby.Id}/start", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await ownerClient.PostAsync($"/api/matching/{lobby.Id}/start", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await guestClient.PostAsJsonAsync($"/api/matching/{lobby.Id}/answer",
                new SubmitMatchingAnswerDto(0, "na"))).StatusCode);
    }

    [Fact]
    public async Task Stable_answer_errors_are_returned_by_the_http_boundary()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"other-{prefix}", $"other-{prefix}@example.com", "Other", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);
        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);
        await ownerClient.PostAsJsonAsync("/api/matching/lobby", new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));

        var badKind = await ownerClient.PostAsJsonAsync("/api/matching/not-created/answer",
            new SubmitMatchingAnswerDto(0, "wat"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badKind.StatusCode);
        Assert.Equal("bad_answer_kind", (await badKind.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
    }

    private static HttpClient Authenticated(MatchingApiTestHost host, Player player)
    {
        var client = host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.TokenIssuer.Issue(player));
        return client;
    }

    private static string Seed(string prefix)
    {
        var categoryId = $"m-{prefix}";
        Shared.MatchingCategories.Items.Add(new MatchingCategory(categoryId, "آزمون", "Test", "x", "#000"));
        for (var i = 0; i < 10; i++)
            Shared.MatchingQuestions.Items.Add(MatchingQuestion.Create($"mq-{prefix}-{i}", Language.En,
                categoryId, $"Prompt {i}", MatchingAnswerSource.Participants, null, Shared.Clock.Now,
                status: QuestionStatus.Approved));
        return categoryId;
    }

    private sealed record ErrorDto(string Error);
}
