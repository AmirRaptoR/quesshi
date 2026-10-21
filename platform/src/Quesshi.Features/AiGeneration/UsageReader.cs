using System.Text.Json;

namespace Quesshi.Features.AiGeneration;

/// <summary>Extracts the token counts and provider-reported USD cost from an AI response.</summary>
public static class UsageReader
{
    public static AiUsage? Read(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("usage", out var usage)) return null;

            return new AiUsage(
                ReadInt(usage, "prompt_tokens"),
                ReadInt(usage, "completion_tokens"),
                ReadDecimal(usage, "cost"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ReadInt(JsonElement usage, string name)
        => usage.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static decimal ReadDecimal(JsonElement usage, string name)
        => usage.TryGetProperty(name, out var value) && value.TryGetDecimal(out var result) ? result : 0m;
}
