using System.Text.Json;

namespace Quesshi.Web.Plugins.Formats.AsyncDuel;

/// <summary>The body of <c>PUT /api/sessions/{id}/settings</c> for an async duel: rules, content and capacity.</summary>
public sealed record AsyncDuelSettingsRequest(JsonElement Rules, JsonElement Content, int Capacity)
{
    /// <summary>Mirrors <c>MatchRules.MinParticipants</c> and <c>MaxParticipants</c>; the server enforces the tenant's own cap.</summary>
    public const int MinCapacity = 2;
    public const int MaxCapacity = 500;

    /// <summary>Mirrors <c>MatchRules.QuestionCountChoices</c>; the server rejects anything else.</summary>
    public static readonly int[] QuestionCounts = [10, 20, 30, 40, 50, 100];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AsyncDuelSettingsRequest From(DuelSettings settings, int capacity)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var content = new
        {
            source = "bank",
            parameters = new { categories = settings.CategoryIds, levels = settings.Levels },
            count = settings.QuestionCount
        };
        return new(
            JsonSerializer.SerializeToElement(new { }, Json),
            JsonSerializer.SerializeToElement(content, Json),
            Math.Clamp(capacity, MinCapacity, MaxCapacity));
    }
}

/// <summary>What the async duel settings need from the platform: the offered categories and the lobby settings call.</summary>
public interface IAsyncDuelSettingsApi
{
    Task<IReadOnlyList<PickerCategory>> CategoriesAsync(string language, CancellationToken cancellationToken = default);

    /// <summary>Returns null on success, else the server's stable error code.</summary>
    Task<string?> SaveAsync(string sessionId, AsyncDuelSettingsRequest request, CancellationToken cancellationToken = default);
}
