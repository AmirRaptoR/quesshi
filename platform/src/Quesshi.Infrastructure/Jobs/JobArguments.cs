using System.Text.Json;

namespace Quesshi.Infrastructure.Jobs;

internal static class JobArguments
{
    public static void Validate(string owner, string kind, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("Job payload must be a JSON object.", nameof(payload));
    }
}
