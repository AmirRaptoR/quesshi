using System.Text.Json;
using System.Text.Json.Serialization;
using Quesshi.Shared;

namespace Quesshi.Kinds.PollChoice;

public sealed class PollChoiceKind : Kind<PollChoicePayload, PollChoiceResolved, PollChoiceCard, PollChoiceAnswer, PollChoiceStored, PollChoiceReveal>, IImports<PollChoicePayload>
{
    private static readonly string[] ImportFields = ["answerSource", "choice1", "choice2", "choice3", "choice4", "choice5", "choice6", "choice7", "choice8"];
    private static readonly ImportExample Example = new(new Dictionary<string, string?>
    {
        ["answerSource"] = "fixed",
        ["choice1"] = "Yes",
        ["choice2"] = "No"
    });

    public override string Id => "poll_choice";
    public override int Version => 1;
    public override string Family => "poll";
    public override bool Gradable => false;
    protected override JsonSerializerContext SerializerContext => PollChoiceJsonContext.Default;
    public IReadOnlyList<string> CsvColumns => ImportFields;
    public IReadOnlyList<string> JsonFields => ImportFields;
    public ImportExample CsvExample => Example;
    public ImportExample JsonExample => Example;

    public bool Matches(string? legacyKind, ImportRow row) => string.Equals(row.Field("answerSource"), "fixed", StringComparison.OrdinalIgnoreCase);

    public PollChoicePayload Convert(ImportRow row) => new(
        Enumerable.Range(1, VotingRules.MaxFixedChoices)
            .Select(number => (Number: number, Text: row.Field($"choice{number}")))
            .Where(choice => choice.Text is not null)
            .Select(choice => new PollChoiceOption($"choice{choice.Number}", choice.Text))
            .ToArray());

    public override PollChoicePayload Upgrade(int from, JsonElement payload)
    {
        if (from != Version) throw new NotSupportedException($"poll_choice v{from} cannot be upgraded by poll_choice v{Version}.");
        var value = JsonSerializer.Deserialize(payload, PollChoiceJsonContext.Default.PollChoicePayload)
            ?? throw new JsonException("Poll choice payload is invalid.");
        return value with { Options = value.Options?.Select(option => new PollChoiceOption(option?.Id?.Trim(), option?.Text?.Trim())).ToArray() };
    }

    public override Snapshot<PollChoiceCard, PollChoiceReveal, PollChoiceStored> UpgradeSnapshot(int from, RawSnapshot old)
    {
        if (from != Version) throw new NotSupportedException($"poll_choice v{from} cannot be upgraded by poll_choice v{Version}.");
        return new(
            JsonSerializer.Deserialize(old.Card, PollChoiceJsonContext.Default.PollChoiceCard) ?? throw new JsonException("Poll choice card is invalid."),
            JsonSerializer.Deserialize(old.Reveal, PollChoiceJsonContext.Default.PollChoiceReveal) ?? throw new JsonException("Poll choice reveal is invalid."),
            old.Answers.Select(answer => JsonSerializer.Deserialize(answer, PollChoiceJsonContext.Default.PollChoiceStored) ?? throw new JsonException("Poll choice stored answer is invalid.")).ToArray());
    }

    public override IReadOnlyList<Problem> Validate(PollChoicePayload payload, ValidateEnv env)
    {
        var problems = new List<Problem>();
        var options = payload.Options ?? [];
        if (options.Count < VotingRules.MinFixedChoices) problems.Add(new("options.tooFew", $"Poll choice needs at least {VotingRules.MinFixedChoices} options."));
        if (options.Count > VotingRules.MaxFixedChoices) problems.Add(new("options.tooMany", $"Poll choice needs at most {VotingRules.MaxFixedChoices} options."));
        if (options.Any(option => string.IsNullOrWhiteSpace(option?.Id) || string.IsNullOrWhiteSpace(option?.Text)))
            problems.Add(new("options.blank", "Poll choice option ids and text cannot be blank."));
        var validIds = options.Where(option => !string.IsNullOrWhiteSpace(option?.Id)).Select(option => option!.Id!.Trim()).ToArray();
        if (validIds.Distinct(StringComparer.Ordinal).Count() != validIds.Length)
            problems.Add(new("options.duplicateId", "Poll choice option ids must be distinct."));
        var validTexts = options.Where(option => !string.IsNullOrWhiteSpace(option?.Text)).Select(option => option!.Text!.Trim()).ToArray();
        if (validTexts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != validTexts.Length)
            problems.Add(new("options.duplicateText", "Poll choice option text must be distinct ignoring case."));
        return problems;
    }

    public override PollChoiceResolved Resolve(PollChoicePayload payload, ResolveContext context) => new();

    public override PollChoiceCard Card(Item<PollChoicePayload, PollChoiceResolved> item) => new(
        (item.Payload.Options ?? throw new InvalidOperationException("Poll choice options are required."))
            .Select(option => option is { } current
                ? new PollChoiceOption(current.Id?.Trim(), current.Text?.Trim())
                : throw new InvalidOperationException("Poll choice options cannot contain null."))
            .ToArray());

    public override PollChoiceReveal Reveal(Item<PollChoicePayload, PollChoiceResolved> item) => new();

    public override PollChoiceStored Normalize(Item<PollChoicePayload, PollChoiceResolved> item, PollChoiceAnswer answer)
    {
        if (answer.Option is { } option && answer.NotApplicable is null)
        {
            if (!(item.Payload.Options ?? []).Any(candidate => candidate?.Id == option))
                throw new MalformedAnswer("Poll choice answer option must identify an authored option.");
            return new(option, null);
        }
        if (answer.Option is null && answer.NotApplicable == true) return new(null, true);
        throw new MalformedAnswer("Poll choice answer must select one option or be not applicable.");
    }

    public override Grade Grade(Item<PollChoicePayload, PollChoiceResolved> item, PollChoiceStored stored) => Quesshi.Shared.Grade.None;
}

public static class VotingRules
{
    public const int MinFixedChoices = 2;
    public const int MaxFixedChoices = 8;
}

public sealed record PollChoicePayload([property: JsonRequired] IReadOnlyList<PollChoiceOption?>? Options);
public sealed record PollChoiceOption(string? Id, string? Text);
public sealed record PollChoiceResolved;
public sealed record PollChoiceCard(IReadOnlyList<PollChoiceOption> Options);
[JsonConverter(typeof(PollChoiceAnswerConverter))]
public sealed record PollChoiceAnswer(string? Option, bool? NotApplicable);
[JsonConverter(typeof(PollChoiceStoredConverter))]
public sealed record PollChoiceStored(string? Option, bool? NotApplicable);
public sealed record PollChoiceReveal;

public sealed class PollChoiceAnswerConverter : PollChoiceAnswerConverterBase<PollChoiceAnswer>
{
    protected override PollChoiceAnswer Create(string? option, bool? notApplicable) => new(option, notApplicable);
    protected override string? Option(PollChoiceAnswer value) => value.Option;
    protected override bool? NotApplicable(PollChoiceAnswer value) => value.NotApplicable;
}

public sealed class PollChoiceStoredConverter : PollChoiceAnswerConverterBase<PollChoiceStored>
{
    protected override PollChoiceStored Create(string? option, bool? notApplicable) => new(option, notApplicable);
    protected override string? Option(PollChoiceStored value) => value.Option;
    protected override bool? NotApplicable(PollChoiceStored value) => value.NotApplicable;
}

public abstract class PollChoiceAnswerConverterBase<T> : JsonConverter<T> where T : class
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Poll choice answer must be an object.");
        string? option = null;
        bool? notApplicable = null;
        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Poll choice answer is malformed.");
            var name = reader.GetString();
            if (!reader.Read()) throw new JsonException("Poll choice answer is malformed.");
            switch (name)
            {
                case "option" when reader.TokenType == JsonTokenType.String && count++ == 0:
                    option = reader.GetString(); break;
                case "notApplicable" when reader.TokenType is JsonTokenType.True && count++ == 0:
                    notApplicable = true; break;
                default: throw new JsonException("Poll choice answer must contain exactly one supported answer.");
            }
        }
        if (reader.TokenType != JsonTokenType.EndObject || count != 1) throw new JsonException("Poll choice answer must contain exactly one supported answer.");
        return Create(option, notApplicable);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (Option(value) is { } option) writer.WriteString("option", option);
        else if (NotApplicable(value) == true) writer.WriteBoolean("notApplicable", true);
        else throw new JsonException("Poll choice stored answer is invalid.");
        writer.WriteEndObject();
    }

    protected abstract T Create(string? option, bool? notApplicable);
    protected abstract string? Option(T value);
    protected abstract bool? NotApplicable(T value);
}

[JsonSerializable(typeof(PollChoicePayload))]
[JsonSerializable(typeof(PollChoiceOption))]
[JsonSerializable(typeof(PollChoiceResolved))]
[JsonSerializable(typeof(PollChoiceCard))]
[JsonSerializable(typeof(PollChoiceAnswer))]
[JsonSerializable(typeof(PollChoiceStored))]
[JsonSerializable(typeof(PollChoiceReveal))]
[JsonSerializable(typeof(Snapshot<PollChoiceCard, PollChoiceReveal, PollChoiceStored>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class PollChoiceJsonContext : JsonSerializerContext;
