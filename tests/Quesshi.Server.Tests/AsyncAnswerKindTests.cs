using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// <see cref="AnswerResultDto"/>, the fifth answer-bearing contract, over the whole real path: an
/// async duel in the silo, the endpoint pipeline on top of it, and the JSON a client would actually
/// read. It is driven end-to-end rather than as a mapping because unlike the other four it is not a
/// pure function of anything a test can hand it — the kind and the answer come from the grain's
/// <see cref="AnswerOutcome"/>, so a test that built one itself would prove only that a record
/// constructor copies fields.
/// <para>
/// The sorting and map rounds here are timed out rather than played — a bare -1 with no response,
/// which is exactly what an async run submits when the clock beats the player. That costs these
/// tests nothing: what is under test is what comes <i>back</i> from answering a sorting or map
/// question, and that is decided by the question, not by what the player sent. The submissions
/// themselves are driven in <see cref="AsyncSubmissionKindTests"/>.
/// </para>
/// </summary>
[Collection(nameof(ClusterCollection))]
public class AsyncAnswerKindTests(ClusterFixture fixture) : IAsyncDisposable
{
    private readonly GameApiTestHost _host = new(fixture.Cluster);

    private static readonly List<string> Rivers = ["Nile", "Amazon", "Yangtze", "Mississippi"];

    /// <summary>
    /// A ten-question duel that opens with a sort, then a map, then eight ordinary choice questions
    /// — the mixed duel the spec describes, where the proportions fall out of the bank rather than a
    /// quota.
    /// </summary>
    private static List<string> SeedMixed(string prefix)
    {
        if (Shared.Categories.Items.All(c => c.Id != "geography"))
            Shared.Categories.Items.Add(new Category("geography", "جغرافیا", "Geography", "globe", "#336699"));

        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            var level = MatchRules.LevelForSlot(slot);
            Shared.Questions.Items.Add(slot switch
            {
                0 => Question.Create(qid, Language.En, "geography", level,
                    "Order these rivers by length, longest first.", Rivers, 0, Shared.Clock.Now,
                    explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Sort),
                1 => Question.Create(qid, Language.En, "geography", level,
                    "Find the country whose capital is Berlin.", [], 0, Shared.Clock.Now,
                    explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Map,
                    target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Borders),
                _ => Question.Create(qid, Language.En, "geography", level,
                    $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, Shared.Clock.Now,
                    explanation: "because", status: QuestionStatus.Approved)
            });
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

    /// <summary>
    /// A fresh duel id whose round-0 shuffle actually shuffles. One id in twenty-four leaves all four
    /// items where they were, and a test asserting that the card is <i>not</i> the stored order would
    /// then fail on a duel that behaved perfectly — an unreproducible failure once every few hundred
    /// runs, which is worse than the seed being slightly chosen.
    /// </summary>
    private static string NewShuffledId()
    {
        while (true)
        {
            var id = Guid.NewGuid().ToString("N");
            if (!SortOrder.IsIdentity(SortOrder.For(id, 0, MatchRules.ChoicesPerQuestion).Served)) return id;
        }
    }

    private async Task<(string Id, HttpClient Mine, HttpClient Theirs)> NewDuelAsync()
    {
        var id = NewShuffledId();
        var questionIds = SeedMixed(id);
        var me = $"aak-me-{id[..8]}";
        var them = $"aak-them-{id[..8]}";

        var grain = fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id);
        await grain.CreateAsync((int)Language.En, me, questionIds, id[..6].ToUpperInvariant());
        Assert.True(await grain.JoinAsync(them));

        return (id, ClientFor(me), ClientFor(them));
    }

    private static async Task<QuestionCardDto> NextAsync(HttpClient client, string id)
    {
        var response = await client.PostAsync($"/api/matches/{id}/next", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<QuestionCardDto>())!;
    }

    private static async Task<AnswerResultDto> AnswerAsync(HttpClient client, string id, int slot, int choice)
    {
        var response = await client.PostAsJsonAsync($"/api/matches/{id}/answer", new AnswerDto(slot, choice));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AnswerResultDto>())!;
    }

    [Fact]
    public async Task Answering_a_sorting_question_comes_back_with_the_kind_and_the_items_in_their_correct_order()
    {
        var (id, mine, _) = await NewDuelAsync();

        var card = await NextAsync(mine, id);
        Assert.Equal((int)QuestionKind.Sort, card.Kind);

        // The endpoint seeded the shuffle with this match and this slot, not with something of its
        // own — the property the three-builder test asserts in the small, asserted here through the
        // whole stack.
        Assert.Equal(SortOrder.For(id, 0, Rivers.Count).Shuffle(Rivers), card.Choices);
        Assert.NotEqual(Rivers, card.Choices);

        var result = await AnswerAsync(mine, id, 0, -1);

        Assert.Equal((int)QuestionKind.Sort, result.Kind);
        Assert.Equal(Rivers, result.CorrectOrder);
        Assert.Null(result.CorrectTarget);

        // The order that comes back is the stored one, which is the answer — not the arrangement the
        // card happened to show, which is what a reveal that reached for the seed would produce.
        Assert.NotEqual(card.Choices, result.CorrectOrder);
    }

    [Fact]
    public async Task Answering_a_map_question_comes_back_with_the_kind_and_the_target()
    {
        var (id, mine, _) = await NewDuelAsync();

        await NextAsync(mine, id);
        await AnswerAsync(mine, id, 0, -1);

        var card = await NextAsync(mine, id);
        Assert.Equal((int)QuestionKind.Map, card.Kind);
        Assert.Equal((int)MapBaseLayer.Borders, card.BaseLayer);
        Assert.Equal((int)MapTargetKind.Country, card.TargetShape);
        Assert.Empty(card.Choices);

        var result = await AnswerAsync(mine, id, 1, -1);

        Assert.Equal((int)QuestionKind.Map, result.Kind);
        Assert.Equal("DE", result.CorrectTarget);
        Assert.Null(result.CorrectOrder);
    }

    /// <summary>
    /// The regression guard for everything that already worked: a choice answer's outcome is what it
    /// always was, with a kind of 0 beside it and nothing else added.
    /// </summary>
    [Fact]
    public async Task Answering_a_choice_question_still_comes_back_as_a_correct_index_alone()
    {
        var (id, mine, _) = await NewDuelAsync();

        for (var slot = 0; slot < 2; slot++)
        {
            await NextAsync(mine, id);
            await AnswerAsync(mine, id, slot, -1);
        }

        await NextAsync(mine, id);
        var result = await AnswerAsync(mine, id, 2, 0);

        Assert.True(result.Correct);
        Assert.Equal(0, result.CorrectIndex);
        Assert.Equal((int)QuestionKind.Choice, result.Kind);
        Assert.Null(result.CorrectOrder);
        Assert.Null(result.CorrectTarget);
    }

    /// <summary>
    /// The duel history over the same real path: once both runs are finished,
    /// <see cref="RevealedQuestionDto"/> knows what kind each question was and what the answer to it
    /// was. The two response fields are null here and correctly so — every sorting and map round in
    /// this duel timed out, and a timeout of any kind stores no response; that they carry a stored
    /// answer when there is one is asserted in <see cref="RevealContractKindTests"/> and, played
    /// through the whole stack, in <see cref="AsyncSubmissionKindTests"/>.
    /// </summary>
    [Fact]
    public async Task The_finished_duels_history_names_the_kind_and_the_answer_of_every_question()
    {
        var (id, mine, theirs) = await NewDuelAsync();

        foreach (var client in new[] { mine, theirs })
            for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
            {
                await NextAsync(client, id);
                await AnswerAsync(client, id, slot, slot < 2 ? -1 : 0);
            }

        var detail = await mine.GetFromJsonAsync<MatchDetailDto>($"/api/matches/{id}");
        Assert.NotNull(detail);
        Assert.Equal(MatchRules.QuestionsPerMatch, detail!.Reveal.Count);

        var sort = detail.Reveal[0];
        Assert.Equal((int)QuestionKind.Sort, sort.Kind);
        // A sort's stored order is its correct order, so Choices is the answer here — the history is
        // the one contract of the five that already carried the items and needs no second copy.
        Assert.Equal(Rivers, sort.Choices);
        Assert.Null(sort.CorrectTarget);

        var map = detail.Reveal[1];
        Assert.Equal((int)QuestionKind.Map, map.Kind);
        Assert.Equal("DE", map.CorrectTarget);
        Assert.Empty(map.Choices);

        var choice = detail.Reveal[2];
        Assert.Equal((int)QuestionKind.Choice, choice.Kind);
        Assert.Equal(0, choice.CorrectIndex);
        Assert.Null(choice.CorrectTarget);
        Assert.Equal(0, choice.MyChoice);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
