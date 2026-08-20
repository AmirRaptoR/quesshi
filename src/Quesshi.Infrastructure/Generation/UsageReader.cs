using System.Text.Json;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Generation;

/// <summary>
/// Pulls the usage block OpenRouter returns when a request asks for it. Cost is in USD and comes
/// from the provider, so it stays right when the model changes; older responses without the block
/// simply record zero rather than guessing at a price table that would rot.
/// </summary>
public static class UsageReader
{
    public static AiCall? Read(string payload, string id, DateTimeOffset at, string model, string purpose)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("usage", out var usage)) return null;

            return new AiCall(id, at,
                doc.RootElement.TryGetProperty("model", out var served) ? served.GetString() ?? model : model,
                purpose,
                Int(usage, "prompt_tokens"),
                Int(usage, "completion_tokens"),
                Decimal(usage, "cost"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Int(JsonElement usage, string name)
        => usage.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static decimal Decimal(JsonElement usage, string name)
        => usage.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : 0m;
}
