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
public sealed class SessionCommands(PlayerApi api, PlatformState state) : Quesshi.Web.Plugins.IFormatCommands
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

    async Task<bool> Quesshi.Web.Plugins.IFormatCommands.SendAsync(string type, object? payload, CancellationToken cancellationToken) =>
        (await SendAsync(type, payload, cancellationToken: cancellationToken)).Succeeded;

    async Task<bool> Quesshi.Web.Plugins.IFormatCommands.ActionAsync(string name, CancellationToken cancellationToken) =>
        (await SendAsync("action", null, name: name, cancellationToken: cancellationToken)).Succeeded;

    /// <summary>
    /// A <c>revision_conflict</c> means the stored answer is newer than the sender's view. The authoritative view is
    /// fetched and accepted, and the command reports as settled so the caller never retries the stale answer.
    /// </summary>
    async Task<bool> Quesshi.Web.Plugins.IFormatCommands.SendAsync(string type, object? payload, int revision, CancellationToken cancellationToken)
    {
        var outcome = await SendAsync(type, payload, revision, cancellationToken: cancellationToken);
        if (outcome.Succeeded) return true;
        return outcome.ErrorCode == "revision_conflict" && await RefreshAsync(cancellationToken);
    }

    private async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        if (state.View is not { } view) return false;
        try
        {
            var response = await api.GetAsync<JsonElement>($"/api/sessions/{view.Id}", cancellationToken);
            if (!response.IsSuccess || PlatformState.ParseView(response.Value.GetRawText()) is not { } fresh) return false;
            state.AcceptView(fresh);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return false;
        }
    }

    private static bool IsRetryable(ApiResponse<JsonElement> response) =>
        response.Error is { } code && RetryableCodes.Contains(code);
}
