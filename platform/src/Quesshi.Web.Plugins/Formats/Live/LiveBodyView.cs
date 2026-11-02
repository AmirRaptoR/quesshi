using System.Text.Json;
using Quesshi.Shared;

namespace Quesshi.Web.Plugins.Formats.Live;

/// <summary>Who answered one slot.</summary>
public sealed record LiveRoundView(int Slot, IReadOnlyList<string> Responded);

/// <summary>The live format's part of a session view, read once per view.</summary>
public sealed record LiveBodyView(
    IReadOnlyList<string> Expected,
    IReadOnlyList<string> Answered,
    string? Kind,
    int? KindVersion,
    JsonElement? Card,
    JsonElement? Reveal,
    JsonElement? Mine,
    LiveRoundView? Current,
    LiveRoundView? Previous)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static LiveBodyView Empty { get; } = new([], [], null, null, null, null, null, null, null);

    /// <summary>An absent, null or malformed body reads as empty, so the screen shows what the envelope alone says.</summary>
    public static LiveBodyView From(SessionView view)
    {
        if (view.Body.ValueKind != JsonValueKind.Object) return Empty;
        try
        {
            var wire = view.Body.Deserialize<Wire>(Options);
            return wire is null ? Empty : new LiveBodyView(wire.Expected ?? [], wire.Answered ?? [], wire.Kind, wire.KindVersion,
                Present(wire.Card), Present(wire.Reveal), Present(wire.Mine), wire.Current, wire.Previous);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static JsonElement? Present(JsonElement? value) => value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } ? value : null;

    private sealed record Wire(IReadOnlyList<string>? Expected, IReadOnlyList<string>? Answered, string? Kind, int? KindVersion,
        JsonElement? Card, JsonElement? Reveal, JsonElement? Mine, LiveRoundView? Current, LiveRoundView? Previous);
}
