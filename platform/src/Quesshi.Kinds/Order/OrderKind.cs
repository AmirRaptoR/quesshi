using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Schema;
using Quesshi.Shared;

namespace Quesshi.Kinds.Order;

public sealed class OrderKind : Kind<OrderPayload, OrderResolved, OrderCard, OrderAnswer, OrderStored, OrderReveal>, IImports<OrderPayload>, IGenerates
{
    private static readonly string[] ImportFields = ["item1", "item2", "item3", "item4"];
    private static readonly JsonElement GenerationSchema = CreateGenerationSchema();
    private static readonly ImportExample Example = new(new Dictionary<string, string?>
    {
        ["item1"] = "First",
        ["item2"] = "Second",
        ["item3"] = "Third",
        ["item4"] = "Fourth"
    });

    public override string Id => "order";
    public override int Version => 1;
    public override string Family => "trivia";
    public override bool Gradable => true;
    protected override JsonSerializerContext SerializerContext => OrderJsonContext.Default;
    public IReadOnlyList<string> CsvColumns => ImportFields;
    public IReadOnlyList<string> JsonFields => ImportFields;
    public ImportExample CsvExample => Example;
    public ImportExample JsonExample => Example;
    public string PromptFragment => "Order exactly four distinct items by one explicit, measurable criterion; state that criterion so the ordering can be checked.";
    public JsonElement CandidateSchema => GenerationSchema;

    public bool Matches(string? legacyKind, ImportRow row) => string.Equals(legacyKind, "sort", StringComparison.OrdinalIgnoreCase);

    public OrderPayload Convert(ImportRow row) => new(
        [new("item1", row.Field("item1")), new("item2", row.Field("item2")), new("item3", row.Field("item3")), new("item4", row.Field("item4"))]);

    public IReadOnlyList<Problem> PostCheck(JsonElement candidate)
    {
        var value = ReadCandidate(candidate);
        var problems = new List<Problem>(Validate(ToPayloadValue(value), new ValidateEnv("en")));
        if (string.IsNullOrWhiteSpace(value.Criterion))
            problems.Add(new("criterion.blank", "Order candidates need a measurable ordering criterion."));
        return problems;
    }

    public JsonElement ToPayload(JsonElement candidate) =>
        JsonSerializer.SerializeToElement(ToPayloadValue(ReadCandidate(candidate)), OrderJsonContext.Default.OrderPayload);

    public override OrderPayload Upgrade(int from, JsonElement payload)
    {
        if (from != Version) throw new NotSupportedException($"order v{from} cannot be upgraded by order v{Version}.");
        var value = JsonSerializer.Deserialize(payload, OrderJsonContext.Default.OrderPayload)
            ?? throw new JsonException("Order payload is invalid.");
        return value with
        {
            Items = value.Items?.Select(item => new OrderItem(item?.Id?.Trim(), item?.Text?.Trim())).ToArray()
        };
    }

    public override Snapshot<OrderCard, OrderReveal, OrderStored> UpgradeSnapshot(int from, RawSnapshot old)
    {
        if (from != Version) throw new NotSupportedException($"order v{from} cannot be upgraded by order v{Version}.");
        return new(
            JsonSerializer.Deserialize(old.Card, OrderJsonContext.Default.OrderCard) ?? throw new JsonException("Order card is invalid."),
            JsonSerializer.Deserialize(old.Reveal, OrderJsonContext.Default.OrderReveal) ?? throw new JsonException("Order reveal is invalid."),
            old.Answers.Select(answer => JsonSerializer.Deserialize(answer, OrderJsonContext.Default.OrderStored) ?? throw new JsonException("Order stored answer is invalid.")).ToArray());
    }

    public override IReadOnlyList<Problem> Validate(OrderPayload payload, ValidateEnv env)
    {
        var problems = new List<Problem>();
        var items = payload.Items ?? [];
        if (items.Count < 4) problems.Add(new("items.tooFew", "Order needs exactly four items."));
        if (items.Count > 4) problems.Add(new("items.tooMany", "Order needs exactly four items."));

        var ids = items.Select(item => item?.Id?.Trim() ?? string.Empty).ToArray();
        var texts = items.Select(item => item?.Text?.Trim() ?? string.Empty).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace)) problems.Add(new("items.blankId", "Order item ids cannot be blank."));
        if (texts.Any(string.IsNullOrWhiteSpace)) problems.Add(new("items.blankText", "Order item text cannot be blank."));
        if (ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Count() != ids.Count(id => !string.IsNullOrWhiteSpace(id)))
            problems.Add(new("items.duplicateId", "Order item ids must be unique."));
        if (texts.Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != texts.Count(text => !string.IsNullOrWhiteSpace(text)))
            problems.Add(new("items.duplicateText", "Order item text must be distinct ignoring case."));
        return problems;
    }

    public override OrderResolved Resolve(OrderPayload payload, ResolveContext context)
    {
        var itemIds = RequiredItems(payload).Select(item => item.Id!).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        for (var i = itemIds.Length - 1; i > 0; i--)
        {
            var j = context.Rng.Next(i + 1);
            (itemIds[i], itemIds[j]) = (itemIds[j], itemIds[i]);
        }
        return new(itemIds);
    }

    public override OrderCard Card(Item<OrderPayload, OrderResolved> item)
    {
        var byId = RequiredItems(item.Payload).ToDictionary(value => value.Id!, StringComparer.Ordinal);
        return new(item.Resolved.ItemIds.Select(id => byId[id]).ToArray());
    }

    public override OrderReveal Reveal(Item<OrderPayload, OrderResolved> item) =>
        new(RequiredItems(item.Payload).Select(value => value.Id!).ToArray());

    public override OrderStored Normalize(Item<OrderPayload, OrderResolved> item, OrderAnswer answer)
    {
        var itemIds = answer.ItemIds;
        var expectedIds = RequiredItems(item.Payload).Select(value => value.Id!).ToHashSet(StringComparer.Ordinal);
        if (itemIds is null || itemIds.Count != 4 || itemIds.Any(string.IsNullOrWhiteSpace) || itemIds.Distinct(StringComparer.Ordinal).Count() != 4 || !itemIds.All(expectedIds.Contains))
            throw new MalformedAnswer("Order answer must place each item id exactly once.");
        return new(itemIds.ToArray());
    }

    public override Grade Grade(Item<OrderPayload, OrderResolved> item, OrderStored stored) =>
        Quesshi.Shared.Grade.Of(stored.ItemIds.SequenceEqual(RequiredItems(item.Payload).Select(value => value.Id!), StringComparer.Ordinal) ? 1 : 0);

    private static IReadOnlyList<OrderItem> RequiredItems(OrderPayload payload) =>
        payload.Items?.Select(item => new OrderItem(item?.Id?.Trim(), item?.Text?.Trim())).ToArray()
        ?? throw new InvalidOperationException("Order items are required.");

    private static OrderCandidate ReadCandidate(JsonElement candidate)
    {
        if (candidate.ValueKind != JsonValueKind.Object) throw new JsonException("Order candidate must be an object.");
        return JsonSerializer.Deserialize(candidate, OrderJsonContext.Default.OrderCandidate)
            ?? throw new JsonException("Order candidate is invalid.");
    }

    private static OrderPayload ToPayloadValue(OrderCandidate candidate) => new(
        candidate.Items?.Select((item, index) => new OrderItem($"item{index + 1}", item?.Trim())).ToArray());

    private static JsonElement CreateGenerationSchema()
    {
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(OrderJsonContext.Default.OrderCandidate);
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }
}

public sealed record OrderPayload([property: JsonRequired] IReadOnlyList<OrderItem>? Items);
public sealed record OrderCandidate([property: JsonRequired] string? Criterion, [property: JsonRequired] IReadOnlyList<string>? Items);
public sealed record OrderItem([property: JsonRequired] string? Id, [property: JsonRequired] string? Text);
public sealed record OrderResolved(IReadOnlyList<string> ItemIds);
public sealed record OrderCard(IReadOnlyList<OrderItem> Items);
public sealed record OrderAnswer([property: JsonRequired] IReadOnlyList<string>? ItemIds);
public sealed record OrderStored([property: JsonRequired] IReadOnlyList<string> ItemIds);
public sealed record OrderReveal(IReadOnlyList<string> ItemIds);

[JsonSerializable(typeof(OrderPayload))]
[JsonSerializable(typeof(OrderCandidate))]
[JsonSerializable(typeof(OrderItem))]
[JsonSerializable(typeof(OrderResolved))]
[JsonSerializable(typeof(OrderCard))]
[JsonSerializable(typeof(OrderAnswer))]
[JsonSerializable(typeof(OrderStored))]
[JsonSerializable(typeof(OrderReveal))]
[JsonSerializable(typeof(Snapshot<OrderCard, OrderReveal, OrderStored>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class OrderJsonContext : JsonSerializerContext;
