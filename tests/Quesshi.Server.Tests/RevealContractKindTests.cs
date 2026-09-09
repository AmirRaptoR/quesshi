using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// A test per answer-bearing contract that a sorting answer and a map answer survive the trip to the
/// wire with their kind and their answer intact — the four contracts whose mapping is a pure
/// function (<see cref="LiveRoundRevealDto"/>, <see cref="LiveRoundResultViewDto"/>,
/// <see cref="LiveRoundAnswerViewDto"/> and <see cref="RevealedQuestionDto"/>). The fifth,
/// <see cref="AnswerResultDto"/>, is built from a grain's outcome and is exercised over real HTTP in
/// <see cref="AsyncAnswerKindTests"/>.
/// <para>
/// These drive the mappers with answers already in place rather than playing a duel, because the
/// submission path that puts a sorting or map answer into an <c>AnswerRecord</c> is issue #74 and
/// does not exist yet. That is not a shortcut around the real path: what these contracts have to get
/// right is the <i>reading</i> of a stored answer, and a stored answer is exactly what they are
/// handed here.
/// </para>
/// <para>
/// Every expectation below is written in stored-index terms, which is the load-bearing claim of the
/// whole design: because submission normalises a sorting answer before storing it, no reveal or
/// history mapper needs the seed. Not one line of this file constructs a <see cref="SortOrder"/>,
/// and if one ever had to, the stored answer would be in the wrong space — a bug to report rather
/// than to work around.
/// </para>
/// </summary>
public class RevealContractKindTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly List<string> Rivers = ["Nile", "Amazon", "Yangtze", "Mississippi"];

    private const string Amir = "rc-amir";
    private const string Sara = "rc-sara";

    private static Question SortQuestion(string id = "sort-q") => Question.Create(id, Language.En, "geography", Difficulty.Medium,
        "Order these rivers by length, longest first.", Rivers, 0, T0,
        explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Sort);

    private static Question MapQuestion(string id = "map-q") => Question.Create(id, Language.En, "geography", Difficulty.Medium,
        "Find the country whose capital is Berlin.", [], 0, T0,
        explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Map,
        target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Borders);

    // ---- LiveRoundRevealDto: the live reveal push ----

    [Fact]
    public void A_sorting_reveal_crosses_with_its_kind_its_correct_order_and_both_players_orders()
    {
        var reveal = new LiveRoundReveal(0, 0, "because",
            [
                new LivePlayerRound(Amir, -1, true, 140, 140, "0,1,2,3"),
                new LivePlayerRound(Sara, -1, false, 0, 0, "1,0,3,2")
            ],
            T0, QuestionKind.Sort, [.. Rivers]);

        var dto = reveal.ToDto();

        Assert.Equal((int)QuestionKind.Sort, dto.Kind);
        Assert.Equal(Rivers, dto.CorrectOrder);
        Assert.Null(dto.CorrectTarget);

        // Each player's own answer survives, and it is readable: "1,0,3,2" against CorrectOrder is
        // Amazon, Nile, Mississippi, Yangtze — which is what a client renders, with no seed anywhere.
        Assert.Equal("0,1,2,3", dto.Players.Single(p => p.PlayerId == Amir).Response);
        Assert.Equal("1,0,3,2", dto.Players.Single(p => p.PlayerId == Sara).Response);
        Assert.Equal(["Amazon", "Nile", "Mississippi", "Yangtze"],
            SortOrder.TryParseOrder(dto.Players.Single(p => p.PlayerId == Sara).Response, 4, out var order)
                ? [.. order.Select(i => dto.CorrectOrder![i])]
                : Array.Empty<string>());
    }

    [Fact]
    public void A_map_reveal_crosses_with_its_kind_its_target_and_both_players_answers()
    {
        var reveal = new LiveRoundReveal(1, 0, "because",
            [
                new LivePlayerRound(Amir, -1, true, 130, 270, "DE"),
                new LivePlayerRound(Sara, -1, false, 0, 0, "FR")
            ],
            T0, QuestionKind.Map, null, "DE");

        var dto = reveal.ToDto();

        Assert.Equal((int)QuestionKind.Map, dto.Kind);
        Assert.Equal("DE", dto.CorrectTarget);
        Assert.Null(dto.CorrectOrder);
        Assert.Equal("DE", dto.Players.Single(p => p.PlayerId == Amir).Response);
        Assert.Equal("FR", dto.Players.Single(p => p.PlayerId == Sara).Response);
    }

    /// <summary>The old kind, unchanged on the wire but for the kind itself.</summary>
    [Fact]
    public void A_choice_reveal_still_crosses_as_a_correct_index_and_nothing_else()
    {
        var dto = new LiveRoundReveal(0, 2, "because",
            [new LivePlayerRound(Amir, 2, true, 150, 150)], T0).ToDto();

        Assert.Equal((int)QuestionKind.Choice, dto.Kind);
        Assert.Equal(2, dto.CorrectIndex);
        Assert.Null(dto.CorrectOrder);
        Assert.Null(dto.CorrectTarget);
        Assert.Null(dto.Players[0].Response);
    }

    // ---- LiveRoundResultViewDto / LiveRoundAnswerViewDto: the reconnect history ----

    [Fact]
    public async Task A_reconnecting_client_gets_the_kind_the_answer_and_every_players_response_back()
    {
        var view = ViewWith(
            Closed(0, "sort-q", (int)QuestionKind.Sort, [.. Rivers], null,
                [new LiveRoundAnswerView(Amir, true, -1, true, 140, "0,1,2,3"),
                 new LiveRoundAnswerView(Sara, true, -1, false, 0, "1,0,3,2")]),
            Closed(1, "map-q", (int)QuestionKind.Map, null, "DE",
                [new LiveRoundAnswerView(Amir, true, -1, true, 130, "DE"),
                 new LiveRoundAnswerView(Sara, true, -1, false, 0, "52.37,4.9")]));

        var dto = await ToDtoAsync(view);

        var sort = dto.Rounds[0];
        Assert.Equal((int)QuestionKind.Sort, sort.Kind);
        Assert.Equal(Rivers, sort.CorrectOrder);
        Assert.Null(sort.CorrectTarget);
        Assert.Equal("0,1,2,3", sort.Answers.Single(a => a.PlayerId == Amir).Response);
        Assert.Equal("1,0,3,2", sort.Answers.Single(a => a.PlayerId == Sara).Response);

        var map = dto.Rounds[1];
        Assert.Equal((int)QuestionKind.Map, map.Kind);
        Assert.Equal("DE", map.CorrectTarget);
        Assert.Null(map.CorrectOrder);
        Assert.Equal("52.37,4.9", map.Answers.Single(a => a.PlayerId == Sara).Response);
    }

    /// <summary>
    /// The redaction rule, restated for the new kinds: the round in flight gives up neither the
    /// answer nor anyone else's response. The grain is what enforces it (see
    /// <see cref="LiveRedactionKindTests"/>, which plays a real duel); this is the mapper's half of
    /// the promise — that it adds nothing of its own to a round the grain has already blanked.
    /// </summary>
    [Fact]
    public async Task The_round_in_flight_carries_no_answer_of_any_kind()
    {
        var view = ViewWith(
            new LiveRoundResultView(0, "sort-q", T0, null,
                [new LiveRoundAnswerView(Amir, true, -1, null, 0), new LiveRoundAnswerView(Sara, false, null, null, 0)]));

        var round = (await ToDtoAsync(view)).Rounds[0];

        Assert.Null(round.CorrectIndex);
        Assert.Null(round.CorrectOrder);
        Assert.Null(round.CorrectTarget);
        Assert.All(round.Answers, a => Assert.Null(a.Response));

        // "They have locked in" still gets through, which is the whole reason Answered exists apart
        // from the answer itself.
        Assert.True(round.Answers.Single(a => a.PlayerId == Amir).Answered);
    }

    // ---- RevealedQuestionDto: the async duel history ----

    [Fact]
    public async Task The_async_history_renders_a_finished_sort_and_map_instead_of_two_blanks()
    {
        var questions = new FakeQuestions();
        var categories = new FakeCategories();
        categories.Items.Add(new Category("geography", "جغرافیا", "Geography", "globe", "#336699"));
        questions.Items.Add(SortQuestion());
        questions.Items.Add(MapQuestion());

        // Both runs finished, so both sides are revealed. Responses are positional against Choices:
        // slot 0 is the sort, slot 1 the map, and the choice index is the -1 both kinds travel with.
        var view = MatchViewWith(["sort-q", "map-q"],
            new RunView(Amir, 280, 2, 2, true, [-1, -1], ["0,1,2,3", "DE"]),
            new RunView(Sara, 0, 0, 2, true, [-1, -1], ["3,2,1,0", "FR"]));

        var reveal = await GameEndpoints.BuildRevealAsync(view, Amir, questions, categories);

        var sort = reveal[0];
        Assert.Equal((int)QuestionKind.Sort, sort.Kind);
        // A sorting question's stored order *is* its correct order, so this contract's own Choices
        // are the answer and there is no second copy of them.
        Assert.Equal(Rivers, sort.Choices);
        Assert.Equal("0,1,2,3", sort.MyResponse);
        Assert.Equal("3,2,1,0", sort.TheirResponse);
        Assert.Null(sort.CorrectTarget);

        var map = reveal[1];
        Assert.Equal((int)QuestionKind.Map, map.Kind);
        Assert.Equal("DE", map.CorrectTarget);
        Assert.Empty(map.Choices);
        Assert.Equal("DE", map.MyResponse);
        Assert.Equal("FR", map.TheirResponse);
    }

    /// <summary>
    /// The other half of the async history's promise: an opponent who has not finished has no
    /// answers in the payload at all, and a sorting or map answer is hidden by that same rule rather
    /// than by one of its own — which is why <c>RunView.Responses</c> is redacted in the same
    /// expression as <c>RunView.Choices</c>.
    /// </summary>
    [Fact]
    public async Task An_unfinished_opponents_sorting_answer_is_absent_exactly_as_their_choices_are()
    {
        var questions = new FakeQuestions();
        var categories = new FakeCategories();
        categories.Items.Add(new Category("geography", "جغرافیا", "Geography", "globe", "#336699"));
        questions.Items.Add(SortQuestion());

        // What MatchGrain.View hands back when the other side is still playing: empty, not null-filled.
        var view = MatchViewWith(["sort-q"],
            new RunView(Amir, 140, 1, 1, true, [-1], ["0,1,2,3"]),
            new RunView(Sara, 0, 0, 0, false, [], []));

        var reveal = await GameEndpoints.BuildRevealAsync(view, Amir, questions, categories);

        Assert.Equal("0,1,2,3", reveal[0].MyResponse);
        Assert.Null(reveal[0].TheirResponse);
        Assert.Null(reveal[0].TheirChoice);
    }

    // ---- scaffolding ----

    private static LiveRoundResultView Closed(int slot, string questionId, int kind, List<string>? order, string? target,
        List<LiveRoundAnswerView> answers)
        // CorrectIndex is 0 for a sort or a map because the domain pins it there — the field keeps
        // its meaning for Choice alone, and these two carry their answer in the fields beside it.
        => new(slot, questionId, T0, 0, answers, kind, order, target);

    private static LiveView ViewWith(params LiveRoundResultView[] rounds) => new(
        "live-1", [Amir, Sara], (int)MatchState.InProgress, (int)LivePhase.Reveal, T0, rounds.Length - 1, rounds.Length,
        [new LivePlayerView(Amir, 270, 2, 0), new LivePlayerView(Sara, 0, 0, 2)],
        [.. rounds], null, false, [], T0, null, "CODE01", (int)Language.En, []);

    /// <summary>The live view mapper with the round in flight left out of the picture — these tests
    /// are about the round history, and a card would only be built for a round that is open.</summary>
    private static Task<LiveViewDto> ToDtoAsync(LiveView view)
        => view.ToLiveDtoAsync(T0, new FakeQuestions(), new FakeCategories(), id => (id, id, false));

    private static MatchView MatchViewWith(List<string> questionIds, params RunView[] runs) => new(
        "async-1", "CODE01", (int)Language.En, [Amir, Sara], (int)MatchState.Resolved, Amir, false, T0,
        questionIds, [.. runs]);
}
