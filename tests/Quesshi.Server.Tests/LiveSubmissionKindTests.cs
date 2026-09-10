using System.Diagnostics;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// The live duel's submission path for all three kinds, played through a real duel in a real silo:
/// what the grain accepts, what it refuses, and what the reveal both players are then shown says.
/// <para>
/// A live duel is where the two things this issue changes are hardest to fake. The sorting shuffle is
/// seeded with the duel's own id, so only a duel that actually exists can be answered correctly —
/// a submission invented in a test would be graded against an arrangement nobody was shown. And a
/// timeout here is not a submission at all: the buzzer closes the round and the duel records the miss
/// itself, which is exactly the case a guard reading <c>ChoiceIndex == -1</c> as "nothing was played"
/// would confuse a sorting answer with.
/// </para>
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveSubmissionKindTests(LiveClusterFixture fixture)
{
    private const string Amir = "lsk-amir";
    private const string Sara = "lsk-sara";

    /// <summary>Stored in their correct order — longest first — which is what makes the stored order
    /// the answer.</summary>
    private static readonly List<string> Rivers = ["Nile", "Amazon", "Yangtze", "Mississippi"];

    private const int SortSlot = 0;
    private const int CountrySlot = 1;
    private const int CitySlot = 2;
    private const int ChoiceSlot = 3;

    private const double CityLatitude = 52.37;
    private const double CityLongitude = 4.9;
    private const double CityRadiusKm = 100;

    /// <summary>A duel that opens with a sort, a country map and a city map, then fills out with
    /// ordinary choice questions.</summary>
    private static List<string> SeedMixed(string prefix)
    {
        if (LiveShared.Categories.Items.All(c => c.Id != "geography"))
            LiveShared.Categories.Items.Add(new Category("geography", "جغرافیا", "Geography", "globe", "#336699"));

        var now = LiveShared.TimeProvider.GetUtcNow();
        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            var level = MatchRules.LevelForSlot(slot);
            LiveShared.Questions.Items.Add(slot switch
            {
                SortSlot => Question.Create(qid, Language.En, "geography", level,
                    "Order these rivers by length, longest first.", Rivers, 0, now,
                    explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Sort),
                CountrySlot => Question.Create(qid, Language.En, "geography", level,
                    "Find the country whose capital is Berlin.", [], 0, now,
                    explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Map,
                    target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Borders),
                CitySlot => Question.Create(qid, Language.En, "geography", level,
                    "Find Amsterdam.", [], 0, now,
                    explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Map,
                    target: MapTarget.City(CityLatitude, CityLongitude, CityRadiusKm), baseLayer: MapBaseLayer.Blank),
                _ => Question.Create(qid, Language.En, "geography", level,
                    $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, now,
                    explanation: "because", status: QuestionStatus.Approved)
            });
            ids.Add(qid);
        }
        return ids;
    }

    /// <summary>
    /// A started duel sitting on round 0, plus its id — which is half the sorting seed, so the tests
    /// need it to work out what a right answer even is.
    /// </summary>
    private async Task<(ILiveMatchGrain Grain, string Id)> StartedDuelAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var questionIds = SeedMixed(id);
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id);

        await grain.CreateAsync(id[..8].ToUpperInvariant(), (int)Language.En, Amir, questionIds);
        Assert.True(await grain.JoinAsync(Sara) is (int)LiveJoinResult.Joined or (int)LiveJoinResult.AlreadyIn);
        await grain.StartAsync(Amir);

        LiveShared.TimeProvider.Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == 0);

        return (grain, id);
    }

    private static async Task<LiveView> WaitForAsync(ILiveMatchGrain grain, Func<LiveView, bool> ready, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        LiveView? last = null;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            last = await grain.GetAsync(Amir);
            if (last is not null && ready(last)) return last;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Condition not reached within {timeoutMs}ms. Last phase={last?.Phase}, round={last?.RoundIndex}.");
    }

    private static LiveRoundReveal RevealFor(string matchId, int slot)
        => LiveShared.Notifier.EventsFor(matchId)
            .Where(e => e.Kind == "RoundRevealed").Select(e => (LiveRoundReveal)e.Payload)
            .Single(r => r.Slot == slot);

    /// <summary>The answer that puts this duel's round-<paramref name="slot"/> items back into their
    /// stored order: each stored item named by the served position it was shown in.</summary>
    private static string CorrectSortSubmission(string matchId, int slot = SortSlot)
        => SortOrder.FormatOrder(SortOrder.For(matchId, slot, Rivers.Count).Inverse);

    /// <summary>Plays the rounds before <paramref name="slot"/> out of the way — both players answer,
    /// which closes each round at once — and leaves the duel sitting on <paramref name="slot"/>.</summary>
    private static async Task ReachRoundAsync(ILiveMatchGrain grain, string id, int slot)
    {
        for (var earlier = 0; earlier < slot; earlier++)
        {
            var (choiceIndex, response) = PlayableAnswerFor(id, earlier);
            Assert.True(await grain.AnswerAsync(Amir, earlier, choiceIndex, response));
            Assert.True(await grain.AnswerAsync(Sara, earlier, choiceIndex, response));
            await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Reveal);

            LiveShared.TimeProvider.Advance(LiveRules.RevealTime + TimeSpan.FromMilliseconds(50));
            await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == earlier + 1);
        }
    }

    /// <summary>Some answer each kind will accept — used only to get past the rounds a test is not
    /// about.</summary>
    private static (int ChoiceIndex, string? Response) PlayableAnswerFor(string matchId, int slot) => slot switch
    {
        SortSlot => (-1, CorrectSortSubmission(matchId)),
        CountrySlot => (-1, "DE"),
        CitySlot => (-1, Geo.Format(CityLatitude, CityLongitude)),
        _ => (0, (string?)null)
    };

    /// <summary>Runs the clock past the buzzer and the network grace, which is what closes a round
    /// nobody answered — the live duel's only timeout, and one no client ever submits.</summary>
    private static async Task LetTheBuzzerSoundAsync(ILiveMatchGrain grain, int slot)
    {
        LiveShared.TimeProvider.Advance(MatchRules.QuestionTime + MatchRules.NetworkGrace + TimeSpan.FromSeconds(1));
        await WaitForAsync(grain, v => v.RoundIndex > slot || v.Phase is (int)LivePhase.Reveal or (int)LivePhase.Over);
    }

    private static LivePlayerRound AnswerOf(LiveRoundReveal reveal, string playerId)
        => reveal.Players.Single(p => p.PlayerId == playerId);

    // ---- Sorting ----

    [Fact]
    public async Task A_sorting_answer_is_graded_on_its_response_and_scores_when_it_is_right()
    {
        var (grain, id) = await StartedDuelAsync();

        // Amir places the items in their stored order; Sara reverses him. Both answer with a
        // ChoiceIndex of -1, which is the whole trap: read as a timeout, both would be wrong.
        Assert.True(await grain.AnswerAsync(Amir, SortSlot, -1, CorrectSortSubmission(id)));
        Assert.True(await grain.AnswerAsync(Sara, SortSlot, -1, SwapFirstTwo(CorrectSortSubmission(id))));
        await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Reveal);

        var reveal = RevealFor(id, SortSlot);
        var amir = AnswerOf(reveal, Amir);
        var sara = AnswerOf(reveal, Sara);

        Assert.True(amir.Correct);
        Assert.True(amir.RoundScore > 0);
        Assert.Equal(amir.RoundScore, amir.TotalScore);

        Assert.False(sara.Correct);
        Assert.Equal(0, sara.RoundScore);
        Assert.Equal(0, sara.TotalScore);

        // Both answers are stored in stored-index terms, which is what lets the reveal index them
        // straight into its own CorrectOrder without ever touching the seed again.
        Assert.Equal("0,1,2,3", amir.Response);
        Assert.NotEqual("0,1,2,3", sara.Response);
        Assert.Equal(-1, amir.ChoiceIndex);
    }

    [Fact]
    public async Task A_sorting_round_nobody_answers_is_a_timeout_with_no_response_at_all()
    {
        var (grain, id) = await StartedDuelAsync();

        await LetTheBuzzerSoundAsync(grain, SortSlot);

        var reveal = RevealFor(id, SortSlot);
        foreach (var player in new[] { Amir, Sara })
        {
            var answer = AnswerOf(reveal, player);
            Assert.False(answer.Correct);
            Assert.Equal(0, answer.RoundScore);

            // The sentinel is the same one a played sorting answer wears; the null response is the
            // only thing that says nobody played.
            Assert.Equal(-1, answer.ChoiceIndex);
            Assert.Null(answer.Response);
        }
    }

    [Theory]
    [InlineData("0,0,1,2")]      // not a permutation
    [InlineData("0,1,2")]        // too short
    [InlineData("0,1,2,4")]      // out of range
    [InlineData("۰,۱,۲,۳")]      // Persian digits, from a Persian browser
    public async Task A_sorting_submission_that_is_not_a_permutation_is_refused_and_the_round_stays_open(string submission)
    {
        var (grain, id) = await StartedDuelAsync();

        Assert.False(await grain.AnswerAsync(Amir, SortSlot, -1, submission));

        // Refused, not recorded as wrong: the round is still his to answer, inside the same 20
        // seconds he already had.
        Assert.True(await grain.AnswerAsync(Amir, SortSlot, -1, CorrectSortSubmission(id)));
        Assert.True(await grain.AnswerAsync(Sara, SortSlot, -1, CorrectSortSubmission(id)));
        await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Reveal);

        Assert.True(AnswerOf(RevealFor(id, SortSlot), Amir).Correct);
    }

    [Fact]
    public async Task A_sorting_answer_with_no_response_is_refused_rather_than_taken_as_a_timeout()
    {
        var (grain, _) = await StartedDuelAsync();

        // A live duel has no client-submitted timeout — the buzzer records the miss. Accepting an
        // empty response here would let a player lock in a guaranteed-wrong answer that also closes
        // the round early for everybody else.
        Assert.False(await grain.AnswerAsync(Amir, SortSlot, -1));
        Assert.False(await grain.AnswerAsync(Amir, SortSlot, 0));
    }

    // ---- Map ----

    [Fact]
    public async Task A_country_answer_is_graded_against_the_target_and_stored_upper_cased()
    {
        var (grain, id) = await StartedDuelAsync();
        await ReachRoundAsync(grain, id, CountrySlot);

        Assert.True(await grain.AnswerAsync(Amir, CountrySlot, -1, "de"));
        Assert.True(await grain.AnswerAsync(Sara, CountrySlot, -1, "FR"));
        await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Reveal);

        var reveal = RevealFor(id, CountrySlot);
        var amir = AnswerOf(reveal, Amir);
        var sara = AnswerOf(reveal, Sara);

        Assert.True(amir.Correct);
        Assert.True(amir.RoundScore > 0);
        Assert.Equal("DE", amir.Response);

        Assert.False(sara.Correct);
        Assert.Equal(0, sara.RoundScore);
        Assert.Equal("FR", sara.Response);
    }

    [Fact]
    public async Task A_map_round_nobody_answers_is_a_timeout_with_no_response_at_all()
    {
        var (grain, id) = await StartedDuelAsync();
        await ReachRoundAsync(grain, id, CountrySlot);

        await LetTheBuzzerSoundAsync(grain, CountrySlot);

        var amir = AnswerOf(RevealFor(id, CountrySlot), Amir);
        Assert.False(amir.Correct);
        Assert.Equal(0, amir.RoundScore);
        Assert.Equal(-1, amir.ChoiceIndex);
        Assert.Null(amir.Response);
    }

    [Fact]
    public async Task A_pin_is_graded_by_distance_and_stored_in_the_canonical_form()
    {
        var (grain, id) = await StartedDuelAsync();
        await ReachRoundAsync(grain, id, CitySlot);

        Assert.True(await grain.AnswerAsync(Amir, CitySlot, -1, " 52.400000 , 4.880 "));
        Assert.True(await grain.AnswerAsync(Sara, CitySlot, -1, Geo.Format(35.7, 51.4))); // Tehran
        await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Reveal);

        var reveal = RevealFor(id, CitySlot);
        var amir = AnswerOf(reveal, Amir);

        Assert.True(amir.Correct);
        Assert.True(amir.RoundScore > 0);
        Assert.Equal("52.4,4.88", amir.Response); // one spelling of a point, whatever the client sent

        Assert.False(AnswerOf(reveal, Sara).Correct);
        Assert.Equal("35.7,51.4", AnswerOf(reveal, Sara).Response);
    }

    [Theory]
    [InlineData("52,37,4,9")]    // a decimal comma: the Persian and German separator
    [InlineData("۵۲.۳۷,۴.۹")]    // Persian digits
    [InlineData("NaN,4.9")]
    [InlineData("91,4.9")]
    public async Task A_coordinate_that_is_not_invariant_is_refused_rather_than_misparsed(string submission)
    {
        var (grain, id) = await StartedDuelAsync();
        await ReachRoundAsync(grain, id, CitySlot);

        // "52,37" read as a thousands-separated 5237 would put the pin in the Indian Ocean and mark
        // the player wrong — in one language only, with nothing anywhere reporting a fault.
        Assert.False(await grain.AnswerAsync(Amir, CitySlot, -1, submission));

        Assert.True(await grain.AnswerAsync(Amir, CitySlot, -1, Geo.Format(CityLatitude, CityLongitude)));
        Assert.True(await grain.AnswerAsync(Sara, CitySlot, -1, Geo.Format(CityLatitude, CityLongitude)));
        await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Reveal);

        Assert.True(AnswerOf(RevealFor(id, CitySlot), Amir).Correct);
    }

    // ---- Choice, unchanged ----

    [Fact]
    public async Task A_choice_round_is_answered_and_scored_exactly_as_it_always_was()
    {
        var (grain, id) = await StartedDuelAsync();
        await ReachRoundAsync(grain, id, ChoiceSlot);

        Assert.True(await grain.AnswerAsync(Amir, ChoiceSlot, 0));
        Assert.True(await grain.AnswerAsync(Sara, ChoiceSlot, 1));
        await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Reveal);

        var reveal = RevealFor(id, ChoiceSlot);
        var amir = AnswerOf(reveal, Amir);
        var sara = AnswerOf(reveal, Sara);

        Assert.True(amir.Correct);
        Assert.Equal(0, amir.ChoiceIndex);
        Assert.True(amir.RoundScore > 0);
        Assert.False(sara.Correct);
        Assert.Equal(1, sara.ChoiceIndex);

        // The one thing that changed for a choice answer: a field beside it that stays null.
        Assert.Null(amir.Response);
        Assert.Null(sara.Response);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(MatchRules.ChoicesPerQuestion)]
    public async Task A_choice_index_outside_the_range_is_still_refused(int choiceIndex)
    {
        var (grain, id) = await StartedDuelAsync();
        await ReachRoundAsync(grain, id, ChoiceSlot);

        // The range rule did not go away when it became a rule about one kind.
        Assert.False(await grain.AnswerAsync(Amir, ChoiceSlot, choiceIndex));
    }

    /// <summary>The right answer with its first two placements swapped — wrong against any shuffle,
    /// rather than a fixed string that would be right one duel in twenty-four.</summary>
    private static string SwapFirstTwo(string submission)
    {
        var parts = submission.Split(',');
        (parts[0], parts[1]) = (parts[1], parts[0]);
        return string.Join(',', parts);
    }
}
