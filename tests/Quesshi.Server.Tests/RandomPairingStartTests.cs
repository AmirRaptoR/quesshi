using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// Issue #104 removed <c>Join</c>'s auto-start, so the two random-pairing paths that used to rely on
/// it — the offline queue here, the live one in <see cref="LiveMatchmakingGrainTests"/> — need to start
/// the duel themselves. Driven over the real <c>POST /api/matches</c> endpoint rather than the grain
/// directly: the pairing branch that calls <c>StartPairedAsync</c> lives inline in
/// <c>GameEndpoints.MapGame</c>, not in an internal static handler this suite can call into like its
/// siblings, so only an HTTP request actually exercises it.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class RandomPairingStartTests(ClusterFixture fixture) : IAsyncDisposable
{
    private readonly GameApiTestHost _host = new(fixture.Cluster);

    private static List<string> SeedQuestions(string prefix)
    {
        if (Shared.Categories.Items.All(c => c.Id != "geography"))
            Shared.Categories.Items.Add(new Category("geography", "جغرافیا", "Geography", "globe", "#336699"));

        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            Shared.Questions.Items.Add(Question.Create(qid, Language.En, "geography", MatchRules.LevelForSlot(slot),
                $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, Shared.Clock.Now,
                status: QuestionStatus.Approved));
            ids.Add(qid);
        }
        return ids;
    }

    private HttpClient ClientFor(string playerId)
    {
        var player = Shared.Players.Items.FirstOrDefault(p => p.Id == playerId);
        if (player is null)
        {
            player = Player.Register(playerId, $"{playerId}@example.com", playerId, Language.En, DateTimeOffset.UtcNow);
            Shared.Players.Items.Add(player);
        }

        var client = _host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.TokenIssuer.Issue(player));
        return client;
    }

    [Fact]
    public async Task Two_offline_players_asking_for_anyone_are_paired_into_a_duel_already_in_progress()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        SeedQuestions("rps-" + tag);

        var first = ClientFor($"rps-p1-{tag}");
        var second = ClientFor($"rps-p2-{tag}");

        var firstResponse = await first.PostAsJsonAsync("/api/matches", new CreateMatchDto(true, "en", [], null, null));
        firstResponse.EnsureSuccessStatusCode();
        var firstSummary = (await firstResponse.Content.ReadFromJsonAsync<MatchSummaryDto>())!;
        Assert.Equal("awaitingopponent", firstSummary.State); // queued, nobody to pair with yet

        var secondResponse = await second.PostAsJsonAsync("/api/matches", new CreateMatchDto(true, "en", [], null, null));
        secondResponse.EnsureSuccessStatusCode();
        var secondSummary = (await secondResponse.Content.ReadFromJsonAsync<MatchSummaryDto>())!;

        // Paired into the first player's own match, already playing -- no Start press by anyone.
        Assert.Equal(firstSummary.Id, secondSummary.Id);
        Assert.Equal("inprogress", secondSummary.State);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
