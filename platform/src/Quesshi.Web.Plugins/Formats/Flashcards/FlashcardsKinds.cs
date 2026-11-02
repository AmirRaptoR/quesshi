using System.Text.Json;
using Quesshi.Kinds.Flashcard;

namespace Quesshi.Web.Plugins.Formats.Flashcards;

/// <summary>Reads the JSON a flashcards view carries into the typed values the flashcard kind components take, and writes a rating back.</summary>
public static class FlashcardsKinds
{
    public const string Flashcard = "flashcard";

    public static KindCard? Card(FlashcardsBodyView body) =>
        body.Kind == Flashcard && body.KindVersion is { } version ? new KindCard(Flashcard, version, new FlashcardCard()) : null;

    public static FlashcardReveal? Reveal(JsonElement? back)
    {
        if (back is not { ValueKind: JsonValueKind.Object } value) return null;
        try { return value.Deserialize(FlashcardJsonContext.Default.FlashcardReveal); }
        catch (JsonException) { return null; }
    }

    /// <summary>The command payload of a rating the flashcard play component produced.</summary>
    public static JsonElement? Answer(object? answer) =>
        answer is FlashcardAnswer rating ? JsonSerializer.SerializeToElement(rating, FlashcardJsonContext.Default.FlashcardAnswer) : null;
}
