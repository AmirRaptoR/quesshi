using System.Text.Json;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.MapPick;
using Quesshi.Kinds.Order;
using Quesshi.Web.Plugins;

namespace Quesshi.Web.Plugins.Formats.Live;

/// <summary>Reads the JSON a live view carries into the typed values the kind components take, and writes their answers back.</summary>
public static class LiveKinds
{
    public static KindCard? Card(string? kind, int? version, JsonElement? card)
    {
        if (kind is null || version is null || card is not { } value) return null;
        object? typed = Try(() => kind switch
        {
            "choice" => value.Deserialize(ChoiceJsonContext.Default.ChoiceCard),
            "order" => value.Deserialize(OrderJsonContext.Default.OrderCard),
            "map_pick" => value.Deserialize(MapPickJsonContext.Default.MapPickCard),
            _ => (object?)null
        });
        return typed is null ? null : new KindCard(kind, version.Value, typed);
    }

    public static object? Reveal(string? kind, JsonElement? reveal)
    {
        if (kind is null || reveal is not { } value) return null;
        return Try(() => kind switch
        {
            "choice" => value.Deserialize(ChoiceJsonContext.Default.ChoiceReveal),
            "order" => value.Deserialize(OrderJsonContext.Default.OrderReveal),
            "map_pick" => value.Deserialize(MapPickJsonContext.Default.MapPickReveal),
            _ => (object?)null
        });
    }

    /// <summary>The viewer's own stored answer as the one-element list the reveal components take; empty when there is none.</summary>
    public static object Answers(string? kind, JsonElement? stored)
    {
        if (kind is null || stored is not { ValueKind: not JsonValueKind.Null } value) return Empty(kind);
        return Try<object?>(() => kind switch
        {
            "choice" => value.Deserialize(ChoiceJsonContext.Default.ChoiceStored) is { } choice ? new[] { choice } : null,
            "order" => value.Deserialize(OrderJsonContext.Default.OrderStored) is { } order ? new[] { order } : null,
            "map_pick" => value.Deserialize(MapPickJsonContext.Default.MapPickStored) is { } pick ? new[] { pick } : null,
            _ => null
        }) ?? Empty(kind);
    }

    /// <summary>The command payload of an answer a kind's play component produced.</summary>
    public static JsonElement? Answer(string kind, object? answer) => answer switch
    {
        ChoiceAnswer choice when kind == "choice" => JsonSerializer.SerializeToElement(choice, ChoiceJsonContext.Default.ChoiceAnswer),
        OrderAnswer order when kind == "order" => JsonSerializer.SerializeToElement(order, OrderJsonContext.Default.OrderAnswer),
        MapPickAnswer pick when kind == "map_pick" => JsonSerializer.SerializeToElement(pick, MapPickJsonContext.Default.MapPickAnswer),
        _ => null
    };

    private static object Empty(string? kind) => kind switch
    {
        "choice" => Array.Empty<ChoiceStored>(),
        "order" => Array.Empty<OrderStored>(),
        "map_pick" => Array.Empty<MapPickStored>(),
        _ => Array.Empty<object>()
    };

    private static T? Try<T>(Func<T?> read)
    {
        try { return read(); }
        catch (JsonException) { return default; }
    }
}
