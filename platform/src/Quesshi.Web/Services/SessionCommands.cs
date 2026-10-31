using System.Net;
using System.Text.Json;
using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>Outcome of one command: the accepted view when the server returned one, otherwise a stable error code.</summary>
public sealed record SessionCommandOutcome(bool Succeeded, string? ErrorCode, SessionView? View, bool ViewAdvanced)
{
    public static SessionCommandOutcome Failure(string code) => new(false, code, null, false);
}

/// <summary>
/// The one command sender every format screen uses. Each command is positioned at the currently
/// accepted view, its response goes through <see cref="PlatformState.AcceptView"/>, and a retryable
/// server error is retried exactly once.
/// </summary>
public sealed class SessionCommands(PlayerApi api, PlatformState state)
{
    private static readonly HashSet<string> RetryableCodes = new(StringComparer.Ordinal)
    {
        "session_busy", "stale_activation", "retryable", "write_failed", "view_unavailable",
    };

    public async Task<SessionCommandOutcome> SendAsync(string type, object? payload = null, int? revision = null, string? name = null,
        CancellationToken cancellationToken = default)
    {
        var view = state.View;
        if (view is null) return SessionCommandOutcome.Failure("no_current_view");
        var path = $"/api/sessions/{view.Id}/commands";
        var body = new { type, at = new { phase = view.Phase, slot = view.Slot, step = view.Step }, revision, name, payload };

        ApiResponse<JsonElement> response;
        try
        {
            response = await api.SendMutationAsync<JsonElement>(HttpMethod.Post, path, body, cancellationToken);
            if (!response.IsSuccess && IsRetryable(response))
                response = await api.SendMutationAsync<JsonElement>(HttpMethod.Post, path, body, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return SessionCommandOutcome.Failure("network_error");
        }

        if (!response.IsSuccess) return SessionCommandOutcome.Failure(response.Error ?? "request_failed");
        SessionView? accepted;
        try { accepted = PlatformState.ParseView(response.Value.GetRawText()); }
        catch (JsonException) { return SessionCommandOutcome.Failure("malformed_response"); }
        if (accepted is null) return SessionCommandOutcome.Failure("malformed_response");
        return new SessionCommandOutcome(true, null, accepted, state.AcceptView(accepted));
    }

    private static bool IsRetryable(ApiResponse<JsonElement> response) =>
        response.Error is { } code && RetryableCodes.Contains(code);
}
