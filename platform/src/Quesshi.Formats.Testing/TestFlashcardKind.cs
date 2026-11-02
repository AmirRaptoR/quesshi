using System.Text.Json;
using Quesshi.Shared;

namespace Quesshi.Formats.Testing;

/// <summary>A stand-in card-family kind with id <c>flashcard</c>: the card shows the front, the reveal holds the back and a rating 0..3 is the answer.</summary>
public sealed class TestFlashcardKind : IKindRuntime
{
    public string Id => "flashcard";
    public int Version => 1;
    public string Family => "card";
    public bool Gradable => false;

    public JsonElement Upgrade(int from, JsonElement payload) => payload.Clone();
    public JsonElement UpgradeSnapshot(int from, JsonElement snapshot) => snapshot.Clone();

    public IReadOnlyList<Problem> Validate(JsonElement payload, ValidateEnv env) =>
        Text(payload, "front") is null || Text(payload, "back") is null ? [new Problem("card.blank", "A card needs a front and a back.")] : [];

    public JsonElement Resolve(JsonElement payload, ResolveContext context) => JsonSerializer.SerializeToElement(new { });

    public JsonElement Card(JsonElement payload, JsonElement resolved) => JsonSerializer.SerializeToElement(new { front = Text(payload, "front") });

    public JsonElement Reveal(JsonElement payload, JsonElement resolved) => JsonSerializer.SerializeToElement(new { back = Text(payload, "back") });

    public JsonElement? Normalize(JsonElement payload, JsonElement resolved, JsonElement? answer)
    {
        if (answer is not { ValueKind: JsonValueKind.Object } value) return null;
        return value.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Number
            && rating.TryGetInt32(out var number) && number is >= 0 and <= 3
            ? JsonSerializer.SerializeToElement(new { rating = number })
            : throw new MalformedAnswer("A rating is an integer from 0 to 3.");
    }

    public Grade Grade(JsonElement payload, JsonElement resolved, JsonElement? stored) => Shared.Grade.None;

    private static string? Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
