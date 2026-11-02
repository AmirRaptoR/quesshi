using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Web.Services;

namespace Quesshi.Web.Plugins.Features.Reports;

/// <summary>The outcome of a report submission; <see cref="Unavailable"/> means the tenant has the feature off.</summary>
public enum ReportOutcome { Reported, Unavailable, Failed }

/// <summary>The client side of <c>POST /api/x/reports</c> and the browser-safe reports capability flag.</summary>
public sealed class ReportsApi(ClientHttp http)
{
    public const string FeatureId = "reports";
    private const string Root = "/api/x/reports";

    /// <summary>The five reasons a player can pick, in display order; each is also the suffix of its <c>report.*</c> text key.</summary>
    public static readonly IReadOnlyList<string> Reasons = ["wronganswer", "unclear", "duplicate", "offensive", "other"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private Task<bool>? enabled;
    private string? antiforgeryToken;

    /// <summary>Whether the tenant has the reports feature on; read once per session, and off when the configuration cannot be read.</summary>
    public Task<bool> IsEnabledAsync() => enabled ??= ReadEnabledAsync();

    public async Task<ReportOutcome> ReportAsync(Guid questionId, string reason, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (string.IsNullOrEmpty(antiforgeryToken) && !await RefreshAntiforgeryAsync(cancellationToken)) return ReportOutcome.Failed;
            using var request = new HttpRequestMessage(HttpMethod.Post, Root) { Content = JsonContent.Create(new ReportRequest(questionId, reason), options: JsonOptions) };
            request.Headers.TryAddWithoutValidation("X-CSRF", antiforgeryToken);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest && attempt == 0 && await IsInvalidAntiforgeryAsync(response, cancellationToken))
            {
                antiforgeryToken = null;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound) return ReportOutcome.Unavailable;
            return response.IsSuccessStatusCode ? ReportOutcome.Reported : ReportOutcome.Failed;
        }

        return ReportOutcome.Failed;
    }

    private async Task<bool> ReadEnabledAsync()
    {
        try
        {
            var config = await http.GetFromJsonAsync<ConfigDocument>("/api/config");
            return config?.Capabilities?.GetValueOrDefault("feature")?.Contains(FeatureId, StringComparer.Ordinal) == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return false;
        }
    }

    private async Task<bool> RefreshAntiforgeryAsync(CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery"), cancellationToken);
        if (!response.IsSuccessStatusCode) return false;
        var document = await response.Content.ReadFromJsonAsync<AntiforgeryDocument>(JsonOptions, cancellationToken);
        antiforgeryToken = document?.Token;
        return !string.IsNullOrEmpty(antiforgeryToken);
    }

    private static async Task<bool> IsInvalidAntiforgeryAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorDocument>(JsonOptions, cancellationToken);
            return string.Equals(error?.Error, "invalid_antiforgery", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record ReportRequest(Guid QuestionId, string Reason);
    private sealed record ConfigDocument(IReadOnlyDictionary<string, string[]>? Capabilities);
    private sealed record AntiforgeryDocument(string? Token);
    private sealed record ErrorDocument(string? Error);
}
