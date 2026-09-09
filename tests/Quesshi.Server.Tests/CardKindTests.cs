using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Grains;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// The two properties issue #73 asks of a card, whatever kind it is: that the three builders agree
/// on what to serve, and that none of them serves the answer.
/// <para>
/// These call the three builders directly rather than driving a duel through each of the two grains.
/// That is deliberate: what has to hold is that the async endpoint, the live view mapper and the
/// live grain produce the same arrangement <i>for the same (match, slot)</i>, and no end-to-end
/// test can put the same match id and slot in front of all three — an async duel and a live duel
/// never share an id. Driving them would prove the property for whichever pair the silos happened
/// to produce, which is not the property at all.
/// </para>
/// </summary>
public class CardKindTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Category Geography = new("geography", "جغرافیا", "Geography", "globe", "#336699");

    private const string MatchId = "cardkind-match";

    /// <summary>
    /// A slot whose permutation is genuinely not the identity. The identity is a legal output of any
    /// honest shuffle, so a redaction test that happened to land on it would pass while proving
    /// nothing; asking for a permuting slot rather than hard-coding one keeps the test truthful even
    /// if the seed's algorithm is ever repinned.
    /// </summary>
    private static readonly int ShuffledSlot =
        Enumerable.Range(0, 50).First(s => !SortOrder.For(MatchId, s, MatchRules.ChoicesPerQuestion).Served.SequenceEqual([0, 1, 2, 3]));

    private static Question SortQuestion() => Question.Create("sort-q", Language.En, "geography", Difficulty.Medium,
        "Order these rivers by length, longest first.", ["Nile", "Amazon", "Yangtze", "Mississippi"], 0, T0,
        explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Sort);

    private static Question CountryQuestion() => Question.Create("map-country-q", Language.En, "geography", Difficulty.Medium,
        "Find the country whose capital is Berlin.", [], 0, T0,
        explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Map,
        target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Borders);

    private static Question CityQuestion() => Question.Create("map-city-q", Language.En, "geography", Difficulty.Medium,
        "Drop a pin on Amsterdam.", [], 0, T0,
        explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Map,
        target: MapTarget.City(52.37, 4.9, 100), baseLayer: MapBaseLayer.Blank);

    private static QuestionCardDto Async(Question question, int slot)
        => GameEndpoints.BuildCard(MatchId, new ServedSlot(slot, question.Id, 20, MatchRules.QuestionsPerMatch), question, Geography);

    private static LiveRoundCardDto LiveView(Question question, int slot)
        => Mappers.BuildLiveCard(MatchId, slot, MatchRules.QuestionsPerMatch, question, Geography, Language.En, T0);

    private static LiveRoundCardDto LivePush(Question question, int slot)
        => LiveMatchGrain.BuildRoundCard(MatchId, new LiveRound(slot, question.Id, T0), question, Geography,
            MatchRules.QuestionsPerMatch).ToDto();

    // ---- One shuffle, three builders ----

    /// <summary>
    /// The regression this issue exists to prevent: a reconnect or a silo restart showing a player
    /// one arrangement and grading them against another, which from the player's seat is the game
    /// marking a right answer wrong.
    /// </summary>
    [Fact]
    public void All_three_card_builders_serve_a_sorting_question_in_the_same_order()
    {
        var question = SortQuestion();
        var expected = SortOrder.For(MatchId, ShuffledSlot, question.Choices.Count).Shuffle(question.Choices);

        Assert.Equal(expected, Async(question, ShuffledSlot).Choices);
        Assert.Equal(expected, LiveView(question, ShuffledSlot).Choices);
        Assert.Equal(expected, LivePush(question, ShuffledSlot).Choices);
    }

    /// <summary>
    /// The same agreement over every slot of a duel, not just the one this file picked. A builder
    /// that had its own off-by-one on the slot — seeding from the round index rather than the slot,
    /// say — would agree with the others on exactly one slot and this is what catches it.
    /// </summary>
    [Fact]
    public void The_three_builders_agree_slot_by_slot_across_a_whole_duel()
    {
        var question = SortQuestion();

        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var async = Async(question, slot).Choices;
            Assert.Equal(async, LiveView(question, slot).Choices);
            Assert.Equal(async, LivePush(question, slot).Choices);
        }
    }

    [Fact]
    public void All_three_card_builders_agree_on_the_kind_and_the_map_fields()
    {
        var question = CityQuestion();

        foreach (var (kind, layer, shape) in new[]
                 {
                     (Async(question, 0).Kind, Async(question, 0).BaseLayer, Async(question, 0).TargetShape),
                     (LiveView(question, 0).Kind, LiveView(question, 0).BaseLayer, LiveView(question, 0).TargetShape),
                     (LivePush(question, 0).Kind, LivePush(question, 0).BaseLayer, LivePush(question, 0).TargetShape)
                 })
        {
            Assert.Equal((int)QuestionKind.Map, kind);
            Assert.Equal((int)MapBaseLayer.Blank, layer);
            Assert.Equal((int)MapTargetKind.City, shape);
        }
    }

    /// <summary>Nothing changes on the wire for the kind that was there before, except the kind itself.</summary>
    [Fact]
    public void A_choice_card_still_serves_its_options_exactly_as_stored_from_all_three()
    {
        var question = Question.Create("choice-q", Language.En, "geography", Difficulty.Easy,
            "Which is the capital of France?", ["Paris", "Lyon", "Nice", "Brest"], 0, T0, status: QuestionStatus.Approved);

        Assert.Equal(question.Choices, Async(question, ShuffledSlot).Choices);
        Assert.Equal(question.Choices, LiveView(question, ShuffledSlot).Choices);
        Assert.Equal(question.Choices, LivePush(question, ShuffledSlot).Choices);

        Assert.Equal((int)QuestionKind.Choice, Async(question, 0).Kind);
        Assert.Null(Async(question, 0).BaseLayer);
        Assert.Null(Async(question, 0).TargetShape);
    }

    // ---- No card of any kind carries the answer ----

    /// <summary>
    /// "Null" is not the assertion worth making here — a field that does not exist is trivially
    /// null. What has to be absent is the <i>content</i>: for a sort, the stored order, which is the
    /// whole answer and which a card could leak either as the items themselves or as the permutation
    /// that maps them back. So this looks at the serialised payload, which is what actually reaches
    /// the player, and asserts neither is in it.
    /// </summary>
    [Fact]
    public void A_sorting_card_carries_neither_the_stored_order_nor_the_permutation()
    {
        var question = SortQuestion();
        var order = SortOrder.For(MatchId, ShuffledSlot, question.Choices.Count);

        foreach (var json in new[]
                 {
                     JsonSerializer.Serialize(Async(question, ShuffledSlot)),
                     JsonSerializer.Serialize(LiveView(question, ShuffledSlot)),
                     JsonSerializer.Serialize(LivePush(question, ShuffledSlot))
                 })
        {
            // The items themselves are the question and have to go out; their stored sequence is the
            // answer and must not. A JSON array is written in order, so the stored sequence being
            // absent from the payload is exactly the claim.
            Assert.DoesNotContain(Serialized(question.Choices), json);
            Assert.Contains(Serialized(order.Shuffle(question.Choices)), json);

            // Nor the permutation under any other name: given it, a client could undo the shuffle
            // and read the stored order straight off the card.
            Assert.DoesNotContain(SortOrder.FormatOrder(order.Served), json);
            Assert.DoesNotContain(SortOrder.FormatOrder(order.Inverse), json);
        }
    }

    [Fact]
    public void A_country_card_carries_the_shape_of_its_target_but_never_the_country()
    {
        var question = CountryQuestion();

        foreach (var json in new[]
                 {
                     JsonSerializer.Serialize(Async(question, 0)),
                     JsonSerializer.Serialize(LiveView(question, 0)),
                     JsonSerializer.Serialize(LivePush(question, 0))
                 })
        {
            Assert.DoesNotContain("\"DE\"", json);
            Assert.DoesNotContain(question.Target!.ToResponse(), json);

            // What it does carry: enough to pick the right interaction, and no more. A country round
            // wants a tap on a region; knowing that says nothing about which region.
            Assert.Contains($"\"targetShape\":{(int)MapTargetKind.Country}", ToWire(json));
        }
    }

    [Fact]
    public void A_city_card_carries_neither_the_coordinates_nor_the_radius()
    {
        var question = CityQuestion();

        foreach (var json in new[]
                 {
                     JsonSerializer.Serialize(Async(question, 0)),
                     JsonSerializer.Serialize(LiveView(question, 0)),
                     JsonSerializer.Serialize(LivePush(question, 0))
                 })
        {
            Assert.DoesNotContain(question.Target!.ToResponse(), json);
            Assert.DoesNotContain("52.37", json);
            Assert.DoesNotContain("4.9", json);

            // The radius is not the answer, but it is how close a pin has to land — which on a world
            // map narrows the search considerably, and is the question's difficulty lever besides.
            Assert.DoesNotContain("100", json);
        }
    }

    /// <summary>A map card has no choices to serve, so it serves none — the answer cannot hide among them.</summary>
    [Fact]
    public void A_map_card_of_either_shape_carries_no_choices()
    {
        foreach (var question in new[] { CountryQuestion(), CityQuestion() })
        {
            Assert.Empty(Async(question, 0).Choices);
            Assert.Empty(LiveView(question, 0).Choices);
            Assert.Empty(LivePush(question, 0).Choices);
        }
    }

    /// <summary>No card of any kind has ever carried the correct index, and none has grown a way to.</summary>
    [Fact]
    public void No_card_contract_has_a_property_that_could_hold_an_answer()
    {
        foreach (var type in new[] { typeof(QuestionCardDto), typeof(LiveRoundCardDto) })
            Assert.DoesNotContain(type.GetProperties(),
                p => p.Name.Contains("Correct") || p.Name.Contains("Answer") || p.Name == "Target" || p.Name == "Order");
    }

    private static string Serialized(IEnumerable<string> items) => JsonSerializer.Serialize(items);

    /// <summary>The API writes camelCase; these tests serialise with the defaults, so the property
    /// name is spelled the way a handler would spell it only after this.</summary>
    private static string ToWire(string json) => json
        .Replace("\"TargetShape\":", "\"targetShape\":")
        .Replace("\"BaseLayer\":", "\"baseLayer\":")
        .Replace("\"Kind\":", "\"kind\":");
}
