using System.Text.Json;
using Quesshi.Shared;

namespace Quesshi.Web.Plugins.Formats.Poll;

/// <summary>The poll format's part of a running session view, read once per view.</summary>
public sealed record PollBodyView(
    string? Kind,
    int? KindVersion,
    JsonElement? Card,
    JsonElement? Answer,
    int? Revision,
    IReadOnlyList<string> AnsweredSeats,
    int? Slot,
    int? TotalSlots)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static PollBodyView Empty { get; } = new(null, null, null, null, null, [], null, null);

    /// <summary>An absent, null or malformed body reads as empty, so the screen shows what the envelope alone says.</summary>
    public static PollBodyView From(SessionView view)
    {
        if (view.Body.ValueKind != JsonValueKind.Object) return Empty;
        try
        {
            var wire = view.Body.Deserialize<Wire>(Options);
            return wire is null ? Empty : new PollBodyView(wire.Kind, wire.KindVersion, Present(wire.Card), Present(wire.Answer),
                wire.Revision, wire.AnsweredSeats ?? [], wire.Slot, wire.TotalSlots);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static JsonElement? Present(JsonElement? value) => value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } ? value : null;

    private sealed record Wire(string? Kind, int? KindVersion, JsonElement? Card, JsonElement? Answer, int? Revision,
        IReadOnlyList<string>? AnsweredSeats, int? Slot, int? TotalSlots);
}
