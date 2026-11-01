using System.Net;
using System.Net.Http.Json;
using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Plugins.Features.Stats;

/// <summary>Registers what the stats plugin contributes to the player shell.</summary>
public static class StatsClient
{
    public static void Register(IFormatClientRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterSlot(ClientSlots.ProfileSection, typeof(StatsProfileSection));
    }
}

public sealed record StatsFormat(string Format, int Played, int Wins, int Losses, int Draws, long Points, int Streak, int BestStreak);

public sealed record StatsCategory(Guid CategoryId, int Asked, double CreditSum, IReadOnlyDictionary<string, string> Names);

public sealed record StatsSummary(Guid PlayerId, IReadOnlyList<StatsFormat> Formats, IReadOnlyList<StatsCategory> Categories, long Total);

/// <summary>The client side of <c>GET /api/x/stats/players/{id}</c>.</summary>
public sealed class StatsApi(ClientHttp http)
{
    /// <summary>The player's summary, or null when the tenant has stats off or the summary cannot be read.</summary>
    public async Task<StatsSummary?> LoadAsync(string playerId, CancellationToken cancellationToken = default)
    {
        using var response = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/x/stats/players/{Uri.EscapeDataString(playerId)}"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<StatsSummary>(cancellationToken: cancellationToken);
    }
}
