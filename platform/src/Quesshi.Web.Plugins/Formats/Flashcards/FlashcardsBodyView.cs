using System.Text.Json;
using Quesshi.Shared;

namespace Quesshi.Web.Plugins.Formats.Flashcards;

/// <summary>The flashcards format's part of a session view: the running card, or the counts once the deck is finished.</summary>
public sealed record FlashcardsBodyView(
    int Slot,
    string Side,
    JsonElement? Card,
    JsonElement? Back,
    int Total,
    string? Kind,
    int? KindVersion,
    IReadOnlyList<int> Counts)
{
    public const int Ratings = 4;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static FlashcardsBodyView Empty { get; } = new(0, "front", null, null, 0, null, null, new int[Ratings]);

    public bool Flipped => Side == "back";

    /// <summary>An absent or malformed body reads as empty; the counts always hold one entry per rating.</summary>
    public static FlashcardsBodyView From(SessionView view)
    {
        if (view.Body.ValueKind != JsonValueKind.Object) return Empty with { Slot = view.Slot };
        try
        {
            var wire = view.Body.Deserialize<Wire>(Options);
            if (wire is null) return Empty with { Slot = view.Slot };
            var counts = new int[Ratings];
            for (var rating = 0; rating < Ratings && wire.Counts is not null && rating < wire.Counts.Count; rating++) counts[rating] = wire.Counts[rating];
            return new FlashcardsBodyView(wire.Slot ?? view.Slot, wire.Side ?? "front", Present(wire.Card), Present(wire.Back), wire.Total ?? 0,
                wire.Kind, wire.KindVersion, counts);
        }
        catch (JsonException)
        {
            return Empty with { Slot = view.Slot };
        }
    }

    /// <summary>The text on the front of the card when the kind's card carries one.</summary>
    public string? Front => Card is { ValueKind: JsonValueKind.Object } card && card.TryGetProperty("front", out var front) && front.ValueKind == JsonValueKind.String
        ? front.GetString() : null;

    private static JsonElement? Present(JsonElement? value) => value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } ? value : null;

    private sealed record Wire(int? Slot, string? Side, JsonElement? Card, JsonElement? Back, int? Total, string? Kind, int? KindVersion, IReadOnlyList<int>? Counts);
}
