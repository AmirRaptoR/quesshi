namespace Quesshi.Web.Services;

/// <summary>Outcome of one lobby action: success, or the server's stable error code.</summary>
public sealed record LobbyActionOutcome(bool Succeeded, string? ErrorCode)
{
    public static LobbyActionOutcome Failure(string code) => new(false, code);
}

/// <summary>
/// The lobby's mutations over the core endpoints. They answer with a result code, not a view: the
/// session stream is what delivers the changed lobby to every browser, this one included.
/// </summary>
public sealed class LobbyActions(PlayerApi api)
{
    public Task<LobbyActionOutcome> StartAsync(string sessionId, CancellationToken cancellationToken = default) =>
        SendAsync($"/api/sessions/{Uri.EscapeDataString(sessionId)}/start", null, cancellationToken);

    public Task<LobbyActionOutcome> LeaveAsync(string sessionId, CancellationToken cancellationToken = default) =>
        SendAsync($"/api/sessions/{Uri.EscapeDataString(sessionId)}/leave", null, cancellationToken);

    public Task<LobbyActionOutcome> InviteAsync(string sessionId, string playerId, CancellationToken cancellationToken = default) =>
        SendAsync($"/api/sessions/{Uri.EscapeDataString(sessionId)}/invites", new { playerId }, cancellationToken);

    private async Task<LobbyActionOutcome> SendAsync(string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            var response = await api.SendMutationAsync<object>(HttpMethod.Post, path, body, cancellationToken);
            return response.IsSuccess ? new LobbyActionOutcome(true, null) : LobbyActionOutcome.Failure(response.Error ?? "request_failed");
        }
        catch (HttpRequestException)
        {
            return LobbyActionOutcome.Failure("network_error");
        }
    }
}
