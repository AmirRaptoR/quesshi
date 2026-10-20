using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Schema;
using Quesshi.Shared;

namespace Quesshi.Kinds.Choice;

public class ChoiceKind : Kind<ChoicePayload, ChoiceResolved, ChoiceCard, ChoiceAnswer, ChoiceStored, ChoiceReveal>, IImports<ChoicePayload>, IGenerates
{
    private static readonly string[] ImportFields = ["choice1", "choice2", "choice3", "choice4", "correctIndex"];
    private static readonly JsonElement GenerationSchema = CreateGenerationSchema();
    private static readonly ImportExample Example = new(new Dictionary<string, string?>
    {
        ["choice1"] = "Mercury",
        ["choice2"] = "Venus",
        ["choice3"] = "Earth",
        ["choice4"] = "Mars",
        ["correctIndex"] = "2"
    });

    public override string Id => "choice";
    public override int Version => 1;
    public override string Family => "trivia";
    public override bool Gradable => true;
    protected override JsonSerializerContext SerializerContext => ChoiceJsonContext.Default;
    public IReadOnlyList<string> CsvColumns => ImportFields;
    public IReadOnlyList<string> JsonFields => ImportFields;
    public ImportExample CsvExample => Example;
    public ImportExample JsonExample => Example;
    public string PromptFragment => "Provide exactly four distinct answer choices and the zero-based correctIndex for the one correct choice.";
    public JsonElement CandidateSchema => GenerationSchema;

    public bool Matches(string? legacyKind, ImportRow row) => string.Equals(legacyKind, "choice", StringComparison.OrdinalIgnoreCase);

    public ChoicePayload Convert(ImportRow row) => new(
        [row.Field("choice1") ?? "", row.Field("choice2") ?? "", row.Field("choice3") ?? "", row.Field("choice4") ?? ""],
        ImportValues.Integer(row, "correctIndex", -1));

    public IReadOnlyList<Problem> PostCheck(JsonElement candidate) => Validate(ToPayloadValue(ReadCandidate(candidate)), new ValidateEnv("en"));

    public JsonElement ToPayload(JsonElement candidate) =>
        JsonSerializer.SerializeToElement(ToPayloadValue(ReadCandidate(candidate)), ChoiceJsonContext.Default.ChoicePayload);

    public override ChoicePayload Upgrade(int from, JsonElement payload)
    {
        if (from != Version) throw new NotSupportedException($"choice v{from} cannot be upgraded by choice v{Version}.");
        var value = JsonSerializer.Deserialize(payload, ChoiceJsonContext.Default.ChoicePayload)
            ?? throw new JsonException("Choice payload is invalid.");
        return value with { Options = value.Options?.Select(option => option?.Trim() ?? string.Empty).ToArray() };
    }

    public override Snapshot<ChoiceCard, ChoiceReveal, ChoiceStored> UpgradeSnapshot(int from, RawSnapshot old)
    {
        if (from != Version) throw new NotSupportedException($"choice v{from} cannot be upgraded by choice v{Version}.");
        return new(
            JsonSerializer.Deserialize(old.Card, ChoiceJsonContext.Default.ChoiceCard) ?? throw new JsonException("Choice card is invalid."),
            JsonSerializer.Deserialize(old.Reveal, ChoiceJsonContext.Default.ChoiceReveal) ?? throw new JsonException("Choice reveal is invalid."),
            old.Answers.Select(answer => JsonSerializer.Deserialize(answer, ChoiceJsonContext.Default.ChoiceStored) ?? throw new JsonException("Choice stored answer is invalid.")).ToArray());
    }

    public override IReadOnlyList<Problem> Validate(ChoicePayload payload, ValidateEnv env)
    {
        var problems = new List<Problem>();
        var options = payload.Options?.Select(option => option?.Trim() ?? string.Empty).ToArray() ?? [];
        if (options.Length < 4) problems.Add(new("options.tooFew", "Choice needs exactly four options."));
        if (options.Length > 4) problems.Add(new("options.tooMany", "Choice needs exactly four options."));
        if (options.Any(string.IsNullOrWhiteSpace)) problems.Add(new("options.blank", "Choice options cannot be blank."));
        if (options.Where(option => !string.IsNullOrWhiteSpace(option)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count(option => !string.IsNullOrWhiteSpace(option)))
            problems.Add(new("options.duplicate", "Choice options must be distinct ignoring case."));
        if (payload.CorrectIndex < 0 || payload.CorrectIndex >= options.Length)
            problems.Add(new("correctIndex.outOfRange", "Choice correctIndex must identify an option."));
        return problems;
    }

    public override ChoiceResolved Resolve(ChoicePayload payload, ResolveContext context) => new();

    public override ChoiceCard Card(Item<ChoicePayload, ChoiceResolved> item) =>
        new((item.Payload.Options ?? throw new InvalidOperationException("Choice options are required.")).Select(option => option.Trim()).ToArray());

    public override ChoiceReveal Reveal(Item<ChoicePayload, ChoiceResolved> item) =>
        new(item.Payload.CorrectIndex, (item.Payload.Options ?? throw new InvalidOperationException("Choice options are required."))[item.Payload.CorrectIndex].Trim());

    public override ChoiceStored Normalize(Item<ChoicePayload, ChoiceResolved> item, ChoiceAnswer answer)
    {
        if (answer.Index < 0 || answer.Index >= (item.Payload.Options ?? throw new InvalidOperationException("Choice options are required.")).Count)
            throw new MalformedAnswer("Choice answer index must identify one of the four options.");
        return new(answer.Index);
    }

    public override Grade Grade(Item<ChoicePayload, ChoiceResolved> item, ChoiceStored stored) =>
        Quesshi.Shared.Grade.Of(stored.Index == item.Payload.CorrectIndex ? 1 : 0);

    private static ChoiceCandidate ReadCandidate(JsonElement candidate)
    {
        if (candidate.ValueKind != JsonValueKind.Object) throw new JsonException("Choice candidate must be an object.");
        return JsonSerializer.Deserialize(candidate, ChoiceJsonContext.Default.ChoiceCandidate)
            ?? throw new JsonException("Choice candidate is invalid.");
    }

    private static ChoicePayload ToPayloadValue(ChoiceCandidate candidate) => new(
        candidate.Choices?.Select(choice => choice?.Trim() ?? string.Empty).ToArray(), candidate.CorrectIndex);

    private static JsonElement CreateGenerationSchema()
    {
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(ChoiceJsonContext.Default.ChoiceCandidate);
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }
}

public sealed record ChoicePayload([property: JsonRequired] IReadOnlyList<string>? Options, [property: JsonRequired] int CorrectIndex);
public sealed record ChoiceCandidate([property: JsonRequired] IReadOnlyList<string>? Choices, [property: JsonRequired] int CorrectIndex);
public sealed record ChoiceResolved;
public sealed record ChoiceCard(IReadOnlyList<string> Options);
public sealed record ChoiceAnswer([property: JsonRequired] int Index);
public sealed record ChoiceStored([property: JsonRequired] int Index);
public sealed record ChoiceReveal(int CorrectIndex, string CorrectOption);

[JsonSerializable(typeof(ChoicePayload))]
[JsonSerializable(typeof(ChoiceCandidate))]
[JsonSerializable(typeof(ChoiceResolved))]
[JsonSerializable(typeof(ChoiceCard))]
[JsonSerializable(typeof(ChoiceAnswer))]
[JsonSerializable(typeof(ChoiceStored))]
[JsonSerializable(typeof(ChoiceReveal))]
[JsonSerializable(typeof(Snapshot<ChoiceCard, ChoiceReveal, ChoiceStored>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class ChoiceJsonContext : JsonSerializerContext;
