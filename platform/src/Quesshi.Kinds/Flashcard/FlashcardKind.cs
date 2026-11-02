using System.Text.Json;
using System.Text.Json.Serialization;
using Quesshi.Shared;

namespace Quesshi.Kinds.Flashcard;

public sealed class FlashcardKind : Kind<FlashcardPayload, FlashcardResolved, FlashcardCard, FlashcardAnswer, FlashcardStored, FlashcardReveal>
{
    public const int MinRating = 0;
    public const int MaxRating = 3;

    public override string Id => "flashcard";
    public override int Version => 1;
    public override string Family => "card";
    public override bool Gradable => false;
    protected override JsonSerializerContext SerializerContext => FlashcardJsonContext.Default;

    public override FlashcardPayload Upgrade(int from, JsonElement payload)
    {
        if (from != Version) throw new NotSupportedException($"flashcard v{from} cannot be upgraded by flashcard v{Version}.");
        var value = JsonSerializer.Deserialize(payload, FlashcardJsonContext.Default.FlashcardPayload)
            ?? throw new JsonException("Flashcard payload is invalid.");
        return value with { Back = value.Back?.Trim() };
    }

    public override Snapshot<FlashcardCard, FlashcardReveal, FlashcardStored> UpgradeSnapshot(int from, RawSnapshot old)
    {
        if (from != Version) throw new NotSupportedException($"flashcard v{from} cannot be upgraded by flashcard v{Version}.");
        return new(
            JsonSerializer.Deserialize(old.Card, FlashcardJsonContext.Default.FlashcardCard) ?? throw new JsonException("Flashcard card is invalid."),
            JsonSerializer.Deserialize(old.Reveal, FlashcardJsonContext.Default.FlashcardReveal) ?? throw new JsonException("Flashcard reveal is invalid."),
            old.Answers.Select(answer => JsonSerializer.Deserialize(answer, FlashcardJsonContext.Default.FlashcardStored) ?? throw new JsonException("Flashcard stored answer is invalid.")).ToArray());
    }

    public override IReadOnlyList<Problem> Validate(FlashcardPayload payload, ValidateEnv env) =>
        string.IsNullOrWhiteSpace(payload.Back) ? [new("back.blank", "Flashcard back text cannot be blank.")] : [];

    public override FlashcardResolved Resolve(FlashcardPayload payload, ResolveContext context) => new();

    public override FlashcardCard Card(Item<FlashcardPayload, FlashcardResolved> item) => new();

    public override FlashcardReveal Reveal(Item<FlashcardPayload, FlashcardResolved> item) =>
        new(item.Payload.Back?.Trim() ?? throw new InvalidOperationException("Flashcard back text is required."));

    public override FlashcardStored Normalize(Item<FlashcardPayload, FlashcardResolved> item, FlashcardAnswer answer) =>
        answer.Rating is >= MinRating and <= MaxRating
            ? new(answer.Rating)
            : throw new MalformedAnswer($"Flashcard rating must be an integer from {MinRating} to {MaxRating}.");

    public override Grade Grade(Item<FlashcardPayload, FlashcardResolved> item, FlashcardStored stored) => Quesshi.Shared.Grade.None;
}

public sealed record FlashcardPayload([property: JsonRequired] string? Back);
public sealed record FlashcardResolved;
public sealed record FlashcardCard;
public sealed record FlashcardAnswer([property: JsonRequired] int Rating);
public sealed record FlashcardStored([property: JsonRequired] int Rating);
public sealed record FlashcardReveal(string Back);

[JsonSerializable(typeof(FlashcardPayload))]
[JsonSerializable(typeof(FlashcardResolved))]
[JsonSerializable(typeof(FlashcardCard))]
[JsonSerializable(typeof(FlashcardAnswer))]
[JsonSerializable(typeof(FlashcardStored))]
[JsonSerializable(typeof(FlashcardReveal))]
[JsonSerializable(typeof(Snapshot<FlashcardCard, FlashcardReveal, FlashcardStored>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class FlashcardJsonContext : JsonSerializerContext;
