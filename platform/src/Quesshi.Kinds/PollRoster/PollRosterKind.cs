using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Schema;
using Quesshi.Shared;

namespace Quesshi.Kinds.PollRoster;

public sealed class PollRosterKind : Kind<PollRosterPayload, PollRosterResolved, PollRosterCard, PollRosterAnswer, PollRosterStored, PollRosterReveal>, IImports<PollRosterPayload>, IGenerates
{
    private const int MaxLegacyChoices = 8;
    private static readonly string[] ImportFields = ["answerSource", "choice1", "choice2", "choice3", "choice4", "choice5", "choice6", "choice7", "choice8"];
    private static readonly JsonElement GenerationSchema = CreateGenerationSchema();
    private static readonly ImportExample Example = new(new Dictionary<string, string?>
    {
        ["answerSource"] = "participants"
    });

    public override string Id => "poll_roster";
    public override int Version => 1;
    public override string Family => "poll";
    public override bool Gradable => false;
    protected override JsonSerializerContext SerializerContext => PollRosterJsonContext.Default;
    public IReadOnlyList<string> CsvColumns => ImportFields;
    public IReadOnlyList<string> JsonFields => ImportFields;
    public ImportExample CsvExample => Example;
    public ImportExample JsonExample => Example;
    public string PromptFragment => "Create a poll whose options are the active participants; do not supply fixed response options.";
    public JsonElement CandidateSchema => GenerationSchema;

    public bool Matches(string? legacyKind, ImportRow row) => string.Equals(row.Field("answerSource"), "participants", StringComparison.OrdinalIgnoreCase);

    public PollRosterPayload Convert(ImportRow row)
    {
        var unexpected = Enumerable.Range(1, MaxLegacyChoices)
            .Select(number => (Name: $"choice{number}", Value: row.Field($"choice{number}")))
            .Where(field => field.Value is not null)
            .ToDictionary(field => field.Name, field => JsonSerializer.SerializeToElement(field.Value));
        return new() { Extra = unexpected.Count == 0 ? null : unexpected };
    }

    public IReadOnlyList<Problem> PostCheck(JsonElement candidate)
    {
        ReadCandidate(candidate);
        return Validate(new PollRosterPayload(), new ValidateEnv("en"));
    }

    public JsonElement ToPayload(JsonElement candidate)
    {
        ReadCandidate(candidate);
        return JsonSerializer.SerializeToElement(new PollRosterPayload(), PollRosterJsonContext.Default.PollRosterPayload);
    }

    public override PollRosterPayload Upgrade(int from, JsonElement payload)
    {
        if (from != Version) throw new NotSupportedException($"poll_roster v{from} cannot be upgraded by poll_roster v{Version}.");
        return JsonSerializer.Deserialize(payload, PollRosterJsonContext.Default.PollRosterPayload)
            ?? throw new JsonException("Poll roster payload is invalid.");
    }

    public override Snapshot<PollRosterCard, PollRosterReveal, PollRosterStored> UpgradeSnapshot(int from, RawSnapshot old)
    {
        if (from != Version) throw new NotSupportedException($"poll_roster v{from} cannot be upgraded by poll_roster v{Version}.");
        return new(
            JsonSerializer.Deserialize(old.Card, PollRosterJsonContext.Default.PollRosterCard) ?? throw new JsonException("Poll roster card is invalid."),
            JsonSerializer.Deserialize(old.Reveal, PollRosterJsonContext.Default.PollRosterReveal) ?? throw new JsonException("Poll roster reveal is invalid."),
            old.Answers.Select(answer => JsonSerializer.Deserialize(answer, PollRosterJsonContext.Default.PollRosterStored) ?? throw new JsonException("Poll roster stored answer is invalid.")).ToArray());
    }

    public override IReadOnlyList<Problem> Validate(PollRosterPayload payload, ValidateEnv env) =>
        payload.Extra is { Count: > 0 } ? [new("payload.unexpected", "Poll roster has no kind-specific payload fields.")] : [];

    public override PollRosterResolved Resolve(PollRosterPayload payload, ResolveContext context) => new(
        context.Roster.Where(seat => seat.Role == "player" && seat.Status == "active")
            .Select(seat => new PollRosterOption(seat.Seat, seat.Name)).ToArray());

    public override PollRosterCard Card(Item<PollRosterPayload, PollRosterResolved> item) => new(item.Resolved.Options);
    public override PollRosterReveal Reveal(Item<PollRosterPayload, PollRosterResolved> item) => new();

    public override PollRosterStored Normalize(Item<PollRosterPayload, PollRosterResolved> item, PollRosterAnswer answer)
    {
        if (answer.Seat is { } seat && answer.Several is null && answer.None is null && answer.NotApplicable is null)
        {
            if (!item.Resolved.Options.Any(option => option.Seat == seat))
                throw new MalformedAnswer("Poll roster answer seat must identify a frozen option.");
            return new(seat, null, null, null);
        }
        if (answer.Seat is null && answer.Several == true && answer.None is null && answer.NotApplicable is null) return new(null, true, null, null);
        if (answer.Seat is null && answer.Several is null && answer.None == true && answer.NotApplicable is null) return new(null, null, true, null);
        if (answer.Seat is null && answer.Several is null && answer.None is null && answer.NotApplicable == true) return new(null, null, null, true);
        throw new MalformedAnswer("Poll roster answer must select one frozen seat or one special answer.");
    }

    public override Grade Grade(Item<PollRosterPayload, PollRosterResolved> item, PollRosterStored stored) => Quesshi.Shared.Grade.None;

    private static PollRosterCandidate ReadCandidate(JsonElement candidate)
    {
        if (candidate.ValueKind != JsonValueKind.Object) throw new JsonException("Poll roster candidate must be an object.");
        return JsonSerializer.Deserialize(candidate, PollRosterJsonContext.Default.PollRosterCandidate)
            ?? throw new JsonException("Poll roster candidate is invalid.");
    }

    private static JsonElement CreateGenerationSchema()
    {
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(PollRosterJsonContext.Default.PollRosterCandidate);
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }
}

public sealed class PollRosterPayload
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
public sealed record PollRosterCandidate;
public sealed record PollRosterOption(int Seat, string Name);
public sealed record PollRosterResolved(IReadOnlyList<PollRosterOption> Options);
public sealed record PollRosterCard(IReadOnlyList<PollRosterOption> Options);
[JsonConverter(typeof(PollRosterAnswerConverter))]
public sealed record PollRosterAnswer(int? Seat, bool? Several, bool? None, bool? NotApplicable);
[JsonConverter(typeof(PollRosterStoredConverter))]
public sealed record PollRosterStored(int? Seat, bool? Several, bool? None, bool? NotApplicable);
public sealed record PollRosterReveal;

public sealed class PollRosterAnswerConverter : PollRosterAnswerConverterBase<PollRosterAnswer>
{
    protected override PollRosterAnswer Create(int? seat, bool? several, bool? none, bool? notApplicable) => new(seat, several, none, notApplicable);
    protected override (int? Seat, bool? Several, bool? None, bool? NotApplicable) Values(PollRosterAnswer value) => (value.Seat, value.Several, value.None, value.NotApplicable);
}

public sealed class PollRosterStoredConverter : PollRosterAnswerConverterBase<PollRosterStored>
{
    protected override PollRosterStored Create(int? seat, bool? several, bool? none, bool? notApplicable) => new(seat, several, none, notApplicable);
    protected override (int? Seat, bool? Several, bool? None, bool? NotApplicable) Values(PollRosterStored value) => (value.Seat, value.Several, value.None, value.NotApplicable);
}

public abstract class PollRosterAnswerConverterBase<T> : JsonConverter<T> where T : class
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Poll roster answer must be an object.");
        int? seat = null; bool? several = null; bool? none = null; bool? notApplicable = null; var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Poll roster answer is malformed.");
            var name = reader.GetString();
            if (!reader.Read()) throw new JsonException("Poll roster answer is malformed.");
            switch (name)
            {
                case "seat" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && count++ == 0: seat = number; break;
                case "several" when reader.TokenType == JsonTokenType.True && count++ == 0: several = true; break;
                case "none" when reader.TokenType == JsonTokenType.True && count++ == 0: none = true; break;
                case "notApplicable" when reader.TokenType == JsonTokenType.True && count++ == 0: notApplicable = true; break;
                default: throw new JsonException("Poll roster answer must contain exactly one supported answer.");
            }
        }
        if (reader.TokenType != JsonTokenType.EndObject || count != 1) throw new JsonException("Poll roster answer must contain exactly one supported answer.");
        return Create(seat, several, none, notApplicable);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var fields = Values(value);
        writer.WriteStartObject();
        if (fields.Seat is { } seat) writer.WriteNumber("seat", seat);
        else if (fields.Several == true) writer.WriteBoolean("several", true);
        else if (fields.None == true) writer.WriteBoolean("none", true);
        else if (fields.NotApplicable == true) writer.WriteBoolean("notApplicable", true);
        else throw new JsonException("Poll roster stored answer is invalid.");
        writer.WriteEndObject();
    }

    protected abstract T Create(int? seat, bool? several, bool? none, bool? notApplicable);
    protected abstract (int? Seat, bool? Several, bool? None, bool? NotApplicable) Values(T value);
}

[JsonSerializable(typeof(PollRosterPayload))]
[JsonSerializable(typeof(PollRosterCandidate))]
[JsonSerializable(typeof(PollRosterOption))]
[JsonSerializable(typeof(PollRosterResolved))]
[JsonSerializable(typeof(PollRosterCard))]
[JsonSerializable(typeof(PollRosterAnswer))]
[JsonSerializable(typeof(PollRosterStored))]
[JsonSerializable(typeof(PollRosterReveal))]
[JsonSerializable(typeof(Snapshot<PollRosterCard, PollRosterReveal, PollRosterStored>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class PollRosterJsonContext : JsonSerializerContext;
