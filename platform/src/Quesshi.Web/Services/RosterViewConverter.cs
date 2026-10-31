using System.Text.Json;
using System.Text.Json.Serialization;
using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>The server writes either roster shape without a discriminator; a <c>seats</c> list marks the complete one.</summary>
public sealed class RosterViewConverter : JsonConverter<RosterView>
{
    public override RosterView Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return root.TryGetProperty("seats", out _)
            ? root.Deserialize<CompleteRosterView>(Without(options))!
            : root.Deserialize<WindowedRosterView>(Without(options))!;
    }

    public override void Write(Utf8JsonWriter writer, RosterView value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value.GetType(), Without(options));

    private static JsonSerializerOptions Without(JsonSerializerOptions options)
    {
        var copy = new JsonSerializerOptions(options);
        copy.Converters.Clear();
        return copy;
    }
}
