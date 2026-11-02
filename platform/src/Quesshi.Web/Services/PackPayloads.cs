using System.Text.Json;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.Order;

namespace Quesshi.Web.Services;

/// <summary>The kinds a player may write into a pack and the typed payloads their editors work on.</summary>
public static class PackPayloads
{
    public static readonly IReadOnlyList<string> Kinds = ["choice", "order"];

    public static object New(string kind) => kind switch
    {
        "choice" => new ChoicePayload(["", ""], 0),
        "order" => new OrderPayload([new OrderItem("one", ""), new OrderItem("two", "")]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static object Read(string kind, JsonElement value) => kind switch
    {
        "choice" => value.Deserialize(ChoiceJsonContext.Default.ChoicePayload) ?? New(kind),
        "order" => value.Deserialize(OrderJsonContext.Default.OrderPayload) ?? New(kind),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static JsonElement Write(string kind, object value) => kind switch
    {
        "choice" => JsonSerializer.SerializeToElement((ChoicePayload)value, ChoiceJsonContext.Default.ChoicePayload),
        "order" => JsonSerializer.SerializeToElement((OrderPayload)value, OrderJsonContext.Default.OrderPayload),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
