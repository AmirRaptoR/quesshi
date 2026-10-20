using System.Text.Json;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.MapPick;
using Quesshi.Kinds.Order;
using Quesshi.Kinds.PollChoice;
using Quesshi.Kinds.PollRoster;

namespace Quesshi.Web.Admin;

internal static class QuestionPayloadCodec
{
    public static object NewPayload(string kind) => kind switch
    {
        "choice" => new ChoicePayload(["", ""], 0),
        "order" => new OrderPayload([new OrderItem("one", ""), new OrderItem("two", "")]),
        "map_pick" => new MapPickPayload(MapBaseLayer.Borders, new MapTarget(MapTargetKind.Country, "")),
        "poll_choice" => new PollChoicePayload([new PollChoiceOption("one", ""), new PollChoiceOption("two", "")]),
        "poll_roster" => new PollRosterPayload(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static object ReadPayload(string kind, JsonElement value) => kind switch
    {
        "choice" => value.Deserialize(ChoiceJsonContext.Default.ChoicePayload) ?? throw new JsonException(),
        "order" => value.Deserialize(OrderJsonContext.Default.OrderPayload) ?? throw new JsonException(),
        "map_pick" => value.Deserialize(MapPickJsonContext.Default.MapPickPayload) ?? throw new JsonException(),
        "poll_choice" => value.Deserialize(PollChoiceJsonContext.Default.PollChoicePayload) ?? throw new JsonException(),
        "poll_roster" => value.Deserialize(PollRosterJsonContext.Default.PollRosterPayload) ?? throw new JsonException(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static JsonElement WritePayload(string kind, object value) => kind switch
    {
        "choice" => JsonSerializer.SerializeToElement((ChoicePayload)value, ChoiceJsonContext.Default.ChoicePayload),
        "order" => JsonSerializer.SerializeToElement((OrderPayload)value, OrderJsonContext.Default.OrderPayload),
        "map_pick" => JsonSerializer.SerializeToElement((MapPickPayload)value, MapPickJsonContext.Default.MapPickPayload),
        "poll_choice" => JsonSerializer.SerializeToElement((PollChoicePayload)value, PollChoiceJsonContext.Default.PollChoicePayload),
        "poll_roster" => JsonSerializer.SerializeToElement((PollRosterPayload)value, PollRosterJsonContext.Default.PollRosterPayload),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static object ReadCard(string kind, JsonElement value) => kind switch
    {
        "choice" => value.Deserialize(ChoiceJsonContext.Default.ChoiceCard) ?? throw new JsonException(),
        "order" => value.Deserialize(OrderJsonContext.Default.OrderCard) ?? throw new JsonException(),
        "map_pick" => value.Deserialize(MapPickJsonContext.Default.MapPickCard) ?? throw new JsonException(),
        "poll_choice" => value.Deserialize(PollChoiceJsonContext.Default.PollChoiceCard) ?? throw new JsonException(),
        "poll_roster" => value.Deserialize(PollRosterJsonContext.Default.PollRosterCard) ?? throw new JsonException(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
