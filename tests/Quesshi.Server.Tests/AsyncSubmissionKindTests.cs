using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// The async duel's submission path for all three kinds, played rather than constructed: a real duel
/// in the silo, the real <c>POST /api/matches/{id}/answer</c> on top of it, and the run the grain
/// actually stored underneath.
/// <para>
/// It is driven end to end because that is where the two halves of this issue meet. A sorting answer
/// only means anything against the round's own shuffle, and the seed for that is
/// <c>(matchId, slot)</c> — reconstructible only in the grain — so a test that handed the grading a
/// permutation of its own would prove that a helper inverts a list, not that the card a player was
/// shown and the answer they were graded on describe the same arrangement. And the wire matters as
/// much as the grading: <c>AnswerDto.Response</c> is new, and a choice answer has to keep travelling
/// exactly as it did.
/// </para>
/// </summary>
[Collection(nameof(ClusterCollection))]
public class AsyncSubmissionKindTests(ClusterFixture fixture) : IAsyncDisposable
{
    private readonly GameApiTestHost _host = new(fixture.Cluster);

    /// <summary>Stored in their correct order — longest first — which is what makes the stored order
    /// the answer.</summary>
    private static readonly List<string> Rivers = ["Nile", "Amazon", "Yangtze", "Mississippi"];

    private const int SortSlot = 0;
    private const int CountrySlot = 1;
    private const int CitySlot = 2;
    private const int ChoiceSlot = 3;

    /// <summary>Amsterdam, and a radius generous enough that a pin a few kilometres out still counts.</summary>
    private const double CityLatitude = 52.37;
    private const double CityLongitude = 4.9;
    private const double CityRadiusKm = 100;

    /// <summary>
    /// A duel that opens with a sort, a country map and a city map, then fills out with ordinary
    /// choice questions — the mixed duel the spec describes, where the proportions fall out of the
    /// bank rather than a quota.
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
                SortSlot => Question.Create(qid, Language.En, "geography", level,
                    "Order these rivers by length, longest first.", Rivers, 0, Shared.Clock.Now,
                    explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Sort),
                CountrySlot => Question.Create(qid, Language.En, "geography", level,
                    "Find the country whose capital is Berlin.", [], 0, Shared.Clock.Now,
                    explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Map,
                    target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Borders),
                CitySlot => Question.Create(qid, Language.En, "geography", level,
                    "Find Amsterdam.", [], 0, Shared.Clock.Now,
                    explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Map,
                    target: MapTarget.City(CityLatitude, CityLongitude, CityRadiusKm), baseLayer: MapBaseLayer.Blank),
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

    private async Task<Duel> NewDuelAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var questionIds = SeedMixed(id);
        var me = $"ask-me-{id[..8]}";

        var grain = fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id);
        await grain.CreateAsync((int)Language.En, me, questionIds, id[..6].ToUpperInvariant());
        Assert.True(await grain.JoinAsync($"ask-them-{id[..8]}"));

        return new Duel(id, me, grain, ClientFor(me), questionIds);
    }

    private sealed record Duel(string Id, string PlayerId, IMatchGrain Grain, HttpClient Client, List<string> QuestionIds)
    {
        /// <summary>The answer that puts the items back into their stored order for this duel's own
        /// round 0: the served position each stored item was shown in, listed stored-first. Submitting
        /// it means "the item you showed me at <c>Inverse[0]</c> goes first", which is the item stored
        /// first — the right one.</summary>
        public string CorrectSortSubmission => SortOrder.FormatOrder(SortOrder.For(Id, SortSlot, Rivers.Count).Inverse);
    }

    /// <summary>Serves the next question of the run, so the answer below it has a slot to land in.</summary>
    private static async Task<QuestionCardDto> NextAsync(Duel duel)
    {
        var response = await duel.Client.PostAsync($"/api/matches/{duel.Id}/next", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<QuestionCardDto>())!;
    }

    private static Task<HttpResponseMessage> PostAnswerAsync(Duel duel, int slot, int choiceIndex, string? response)
        => duel.Client.PostAsJsonAsync($"/api/matches/{duel.Id}/answer", new AnswerDto(slot, choiceIndex, response));

    private static async Task<AnswerResultDto> AnswerAsync(Duel duel, int slot, int choiceIndex, string? response = null)
    {
        var http = await PostAnswerAsync(duel, slot, choiceIndex, response);
        http.EnsureSuccessStatusCode();
        return (await http.Content.ReadFromJsonAsync<AnswerResultDto>())!;
    }

    /// <summary>Plays the run up to <paramref name="slot"/>, timing every earlier question out, then
    /// serves the question at <paramref name="slot"/> and hands back the card the player was shown.</summary>
    private static async Task<QuestionCardDto> ReachAsync(Duel duel, int slot)
    {
        for (var earlier = 0; earlier < slot; earlier++)
        {
            await NextAsync(duel);
            await AnswerAsync(duel, earlier, -1);
        }
        return await NextAsync(duel);
    }

    /// <summary>What the grain actually stored for one slot of the caller's own run — the answer as
    /// every later reader (the reveal, the duel history) will see it.</summary>
    private async Task<(int ChoiceIndex, string? Response)> StoredAsync(Duel duel, int slot)
    {
        var view = await duel.Grain.GetAsync(duel.PlayerId);
        var run = view!.Runs.Single(r => r.PlayerId == duel.PlayerId);
        return (run.Choices[slot], run.Responses![slot]);
    }

    private static Question QuestionAt(Duel duel, int slot) => Shared.Questions.Items.Single(q => q.Id == duel.QuestionIds[slot]);

    // ---- Sorting ----

    [Fact]
    public async Task A_sorting_answer_in_the_right_order_is_correct_and_scores()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, SortSlot);

        var result = await AnswerAsync(duel, SortSlot, -1, duel.CorrectSortSubmission);

        Assert.True(result.Correct);
        Assert.True(result.Score > 0);
        Assert.Equal(result.Score, result.RunScore);

        // Normalised on the way in: what is stored is the answer in stored-index terms, which for a
        // right answer is the identity order and nothing a reader needs a seed to interpret.
        Assert.Equal((-1, "0,1,2,3"), await StoredAsync(duel, SortSlot));

        var question = QuestionAt(duel, SortSlot);
        Assert.Equal(1, question.TimesServed);
        Assert.Equal(1, question.TimesCorrect);
    }

    [Fact]
    public async Task A_sorting_answer_in_the_wrong_order_is_wrong_and_scores_nothing()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, SortSlot);

        // The right answer with its first two placements swapped — wrong for any shuffle, rather than
        // a fixed string that would be right one duel in twenty-four.
        var correct = SortOrder.For(duel.Id, SortSlot, Rivers.Count).Inverse.ToList();
        (correct[0], correct[1]) = (correct[1], correct[0]);

        var result = await AnswerAsync(duel, SortSlot, -1, SortOrder.FormatOrder(correct));

        Assert.False(result.Correct);
        Assert.Equal(0, result.Score);
        Assert.Equal(0, result.RunScore);

        var (choiceIndex, response) = await StoredAsync(duel, SortSlot);
        Assert.Equal(-1, choiceIndex);
        Assert.NotNull(response);
        Assert.NotEqual("0,1,2,3", response); // stored, and stored wrong
        Assert.True(SortOrder.TryParseOrder(response, Rivers.Count, out _));

        var question = QuestionAt(duel, SortSlot);
        Assert.Equal(1, question.TimesServed);
        Assert.Equal(0, question.TimesCorrect);
    }

    [Fact]
    public async Task A_sorting_question_that_times_out_is_wrong_and_stores_no_response()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, SortSlot);

        var result = await AnswerAsync(duel, SortSlot, -1);

        Assert.False(result.Correct);
        Assert.Equal(0, result.Score);

        // The pair that must stay distinguishable: this and the played answer above are both -1, and
        // only the response says which is which.
        Assert.Equal((-1, (string?)null), await StoredAsync(duel, SortSlot));
        Assert.Equal(0, QuestionAt(duel, SortSlot).TimesCorrect);
    }

    [Fact]
    public async Task A_played_sorting_answer_is_graded_rather_than_read_as_a_timeout()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, SortSlot);

        // The whole trap of this issue in one assertion. Both this and the test above submit
        // ChoiceIndex -1; the old guard read that as "the clock ran out" before anything looked at
        // the kind, which would have marked this one wrong too.
        var result = await AnswerAsync(duel, SortSlot, -1, duel.CorrectSortSubmission);

        Assert.True(result.Correct);
        Assert.True(result.Score > 0);
    }

    [Fact]
    public async Task A_submission_of_served_positions_is_stored_in_stored_index_terms()
    {
        var duel = await NewDuelAsync();
        var card = await ReachAsync(duel, SortSlot);

        var order = SortOrder.For(duel.Id, SortSlot, Rivers.Count);

        // "2,0,3,1" means "the item you showed me third goes first, the first one second, ...". The
        // stored form of that is the stored index of each of those served items, in the same order.
        await AnswerAsync(duel, SortSlot, -1, "2,0,3,1");

        var expected = SortOrder.FormatOrder([order.StoredIndexAt(2), order.StoredIndexAt(0), order.StoredIndexAt(3), order.StoredIndexAt(1)]);
        var (_, stored) = await StoredAsync(duel, SortSlot);
        Assert.Equal(expected, stored);

        // And the reading is the one the spec pins rather than its inverse: the stored answer's first
        // entry names the item the card showed third, which is what the player said goes first.
        var placedFirst = card.Choices[2];
        Assert.Equal(placedFirst, Rivers[int.Parse(stored!.Split(',')[0])]);
    }

    [Theory]
    [InlineData("0,0,1,2")]      // not a permutation: an index used twice
    [InlineData("0,1,2")]        // too short
    [InlineData("0,1,2,3,0")]    // too long
    [InlineData("0,1,2,4")]      // out of range
    [InlineData("۰,۱,۲,۳")]      // Persian digits, from a Persian browser
    [InlineData("0;1;2;3")]      // not the format at all
    public async Task A_sorting_submission_that_is_not_a_permutation_is_refused_and_nothing_is_stored(string submission)
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, SortSlot);

        var refused = await PostAnswerAsync(duel, SortSlot, -1, submission);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("bad_response", (await refused.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        // Refused, not recorded as wrong: the slot is still open and the player can still answer it,
        // and the question's own counters never moved.
        Assert.Equal(0, QuestionAt(duel, SortSlot).TimesServed);
        var result = await AnswerAsync(duel, SortSlot, -1, duel.CorrectSortSubmission);
        Assert.True(result.Correct);
    }

    // ---- Map ----

    [Fact]
    public async Task A_country_answer_that_hits_the_target_is_correct_and_scores()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, CountrySlot);

        var result = await AnswerAsync(duel, CountrySlot, -1, "de");

        Assert.True(result.Correct);
        Assert.True(result.Score > 0);

        // Stored the way the target itself spells it, so the history renders one spelling of an
        // answer however the client happened to send it.
        Assert.Equal((-1, "DE"), await StoredAsync(duel, CountrySlot));
        Assert.Equal(1, QuestionAt(duel, CountrySlot).TimesCorrect);
    }

    [Fact]
    public async Task A_country_answer_that_misses_is_wrong_and_still_stored()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, CountrySlot);

        var result = await AnswerAsync(duel, CountrySlot, -1, "FR");

        Assert.False(result.Correct);
        Assert.Equal(0, result.Score);
        Assert.Equal((-1, "FR"), await StoredAsync(duel, CountrySlot));
        Assert.Equal(0, QuestionAt(duel, CountrySlot).TimesCorrect);
    }

    [Fact]
    public async Task A_map_question_that_times_out_is_wrong_and_stores_no_response()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, CountrySlot);

        var result = await AnswerAsync(duel, CountrySlot, -1);

        Assert.False(result.Correct);
        Assert.Equal(0, result.Score);
        Assert.Equal((-1, (string?)null), await StoredAsync(duel, CountrySlot));
    }

    [Fact]
    public async Task A_pin_inside_the_tolerance_is_correct_and_stored_in_the_canonical_form()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, CitySlot);

        // A few kilometres out — well inside the radius, and written with trailing zeroes and spaces
        // the canonical form does not keep.
        var result = await AnswerAsync(duel, CitySlot, -1, " 52.400000 , 4.880 ");

        Assert.True(result.Correct);
        Assert.True(result.Score > 0);
        Assert.Equal((-1, "52.4,4.88"), await StoredAsync(duel, CitySlot));
        Assert.Equal(1, QuestionAt(duel, CitySlot).TimesCorrect);
    }

    [Fact]
    public async Task A_pin_outside_the_tolerance_is_wrong_and_still_stored()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, CitySlot);

        var result = await AnswerAsync(duel, CitySlot, -1, Geo.Format(35.7, 51.4)); // Tehran

        Assert.False(result.Correct);
        Assert.Equal(0, result.Score);
        Assert.Equal((-1, "35.7,51.4"), await StoredAsync(duel, CitySlot));
    }

    [Theory]
    [InlineData("52,37,4,9")]        // a decimal comma: the Persian and German separator
    [InlineData("۵۲.۳۷,۴.۹")]        // Persian digits
    [InlineData("52.37")]            // one coordinate
    [InlineData("52.37,4.9,100")]    // three
    [InlineData("NaN,4.9")]          // parses under the invariant culture, and would make every comparison false
    [InlineData("91,4.9")]           // off the planet
    [InlineData("DE")]               // the other target shape
    public async Task A_coordinate_that_is_not_invariant_is_refused_rather_than_misparsed(string submission)
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, CitySlot);

        var refused = await PostAnswerAsync(duel, CitySlot, -1, submission);

        // Refused, not misparsed: "52,37" read as a thousands-separated 5237 would land the player in
        // the Indian Ocean and be marked wrong, in one language only, with nothing reporting a fault.
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("bad_response", (await refused.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
        Assert.Equal(0, QuestionAt(duel, CitySlot).TimesServed);
    }

    [Fact]
    public async Task A_country_answer_that_is_not_a_country_code_is_refused()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, CountrySlot);

        var refused = await PostAnswerAsync(duel, CountrySlot, -1, "Germany");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, QuestionAt(duel, CountrySlot).TimesServed);
    }

    // ---- Choice, unchanged ----

    [Fact]
    public async Task A_choice_answer_travels_and_is_stored_exactly_as_it_always_was()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, ChoiceSlot);

        // The wire shape a client that predates this issue sends: a slot and an index, no response
        // field at all.
        var http = await duel.Client.PostAsJsonAsync($"/api/matches/{duel.Id}/answer", new { slot = ChoiceSlot, choiceIndex = 0 });
        http.EnsureSuccessStatusCode();
        var result = (await http.Content.ReadFromJsonAsync<AnswerResultDto>())!;

        Assert.True(result.Correct);
        Assert.Equal(0, result.CorrectIndex);
        Assert.Equal((int)QuestionKind.Choice, result.Kind);
        Assert.True(result.Score > 0);

        // The index is the answer and the response stays null — a choice answer gains nothing but a
        // field it never fills.
        Assert.Equal((0, (string?)null), await StoredAsync(duel, ChoiceSlot));
    }

    [Fact]
    public async Task A_response_attached_to_a_choice_answer_is_ignored_rather_than_stored()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, ChoiceSlot);

        var result = await AnswerAsync(duel, ChoiceSlot, 0, "0,1,2,3");

        // The choice index is still the whole answer. Storing the stray string beside it would leave
        // two fields claiming to be the answer, with nothing to make them agree.
        Assert.True(result.Correct);
        Assert.Equal((0, (string?)null), await StoredAsync(duel, ChoiceSlot));
    }

    [Fact]
    public async Task A_timed_out_choice_answer_is_still_the_bare_sentinel()
    {
        var duel = await NewDuelAsync();
        await ReachAsync(duel, ChoiceSlot);

        var result = await AnswerAsync(duel, ChoiceSlot, -1);

        Assert.False(result.Correct);
        Assert.Equal(0, result.Score);
        Assert.Equal((-1, (string?)null), await StoredAsync(duel, ChoiceSlot));
    }

    private sealed record ErrorDto(string Error);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
