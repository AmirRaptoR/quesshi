using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Plugins.Features.Leaderboard;

/// <summary>A board read: the page, or <see cref="Unavailable"/> when the server cannot serve the requested scope.</summary>
public sealed record LeaderboardResult(LeaderboardDto? Board, bool Unavailable)
{
    public static readonly LeaderboardResult NotAvailable = new(null, true);
}

/// <summary>The client side of <c>GET /api/x/leaderboard/{board}</c> and the session effects report.</summary>
public sealed class LeaderboardApi(ClientHttp http)
{
    public const string FeatureId = "leaderboard";
    public const string AllBoard = "all";
    public const string AllPeriod = "all";
    public const string AllScope = "all";
    public const string FriendsScope = "friends";

    public async Task<LeaderboardResult> LoadAsync(string board, string period, string scope, CancellationToken cancellationToken = default)
    {
        var path = $"/api/x/leaderboard/{Uri.EscapeDataString(board)}?period={Uri.EscapeDataString(period)}&scope={Uri.EscapeDataString(scope)}";
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return LeaderboardResult.NotAvailable;
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<LeaderboardDto>(cancellationToken: cancellationToken);
        return new LeaderboardResult(page ?? new LeaderboardDto([], null), false);
    }

    /// <summary>The state of this feature's effects for a session; null when the server does not report effects for the viewer.</summary>
    public async Task<string?> EffectStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var response = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{Uri.EscapeDataString(sessionId)}/effects"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var effects = await response.Content.ReadFromJsonAsync<EffectsReport>(cancellationToken: cancellationToken);
        return effects?.Features?.FirstOrDefault(f => f.FeatureId == FeatureId)?.Status ?? "off";
    }

    private sealed record EffectsReport(long Revision, IReadOnlyList<EffectFeature>? Features);

    private sealed record EffectFeature(string FeatureId, string Status);
}

/// <summary>The period choices of the Ranks page: all time, the current month and the current ISO week.</summary>
public static class LeaderboardPeriods
{
    public static IReadOnlyList<string> Current(DateTimeOffset now)
    {
        var date = now.UtcDateTime.Date;
        var week = $"{ISOWeek.GetYear(date):D4}-W{ISOWeek.GetWeekOfYear(date):D2}";
        return [LeaderboardApi.AllPeriod, date.ToString("yyyy-MM", CultureInfo.InvariantCulture), week];
    }
}
