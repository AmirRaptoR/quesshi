using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quesshi.Features.Features.Matchmaking;

/// <summary>The settings a waiting <c>live</c> player stores; language and question count decide who matches.</summary>
public sealed record LiveQueueSettings(
    string Language,
    int QuestionCount,
    IReadOnlyList<Guid> Categories,
    IReadOnlyList<int> Levels);

/// <summary>What a client may choose when it joins the <c>live</c> queue.</summary>
public sealed record LiveQueueRequest(int QuestionCount, IReadOnlyList<Guid>? Categories = null, IReadOnlyList<int>? Levels = null);

[JsonSerializable(typeof(LiveQueueSettings))]
[JsonSerializable(typeof(LiveQueueRequest))]
[JsonSerializable(typeof(BankParameters))]
[JsonSerializable(typeof(WaitingItem))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class MatchmakingJsonContext : JsonSerializerContext;

/// <summary>The content parameters the bank source reads for a matchmade session.</summary>
public sealed record BankParameters(IReadOnlyList<Guid>? Categories, IReadOnlyList<int>? Levels);

/// <summary>The inbox payload shown while a player waits for an opponent.</summary>
public sealed record WaitingItem(string Format, DateTimeOffset EnqueuedAt);

/// <summary>How a format's players are matched. Only the create-started mode exists so far.</summary>
internal static class MatchmakingRules
{
    public const string Live = "live";

    private static readonly int[] QuestionCounts = [10, 20, 30, 40, 50, 100];

    public static bool IsCreateStarted(string format) => string.Equals(format, Live, StringComparison.Ordinal);

    /// <summary>Parses and validates the client's request, adding the player's language.</summary>
    public static LiveQueueSettings? ParseLive(JsonElement raw, string language)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        LiveQueueRequest? request;
        try { request = raw.Deserialize(MatchmakingJsonContext.Default.LiveQueueRequest); }
        catch (JsonException) { return null; }
        if (request is null || Array.IndexOf(QuestionCounts, request.QuestionCount) < 0) return null;

        var categories = (request.Categories ?? []).Distinct().ToArray();
        var levels = (request.Levels ?? []).Distinct().Order().ToArray();
        if (categories.Length > 3 || categories.Contains(Guid.Empty) || levels.Any(level => level is < 1 or > 5)) return null;
        return new LiveQueueSettings(language, request.QuestionCount, categories, levels);
    }

    /// <summary>The key two waiting players share: the format's match rule applied to their settings.</summary>
    public static string MatchKey(string format, LiveQueueSettings settings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{format}\n{settings.Language}\n{settings.QuestionCount}"))).ToLowerInvariant();
}
