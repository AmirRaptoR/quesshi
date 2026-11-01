using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Web.Services;

namespace Quesshi.Web.Plugins.Features.Friends;

public sealed record FriendEntry(Guid Id, string DisplayName, string AvatarSeed, bool IsOnline);

public sealed record FriendCandidate(Guid Id, string DisplayName, string AvatarSeed);

/// <summary>The outcome of a friends request; <see cref="Unavailable"/> means the tenant has the feature off.</summary>
public enum FriendsOutcome { Ok, Unavailable, Failed }

/// <summary>Calls the friends feature routes through the shared client HTTP boundary.</summary>
public sealed class FriendsClient(ClientHttp http)
{
    private const string Root = "/api/x/friends";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string? antiforgeryToken;

    public async Task<(FriendsOutcome Outcome, IReadOnlyList<FriendEntry> Friends)> ListAsync(CancellationToken cancellationToken = default)
    {
        var (outcome, value) = await ReadAsync<FriendEntry[]>(Root, cancellationToken);
        return (outcome, value ?? []);
    }

    public async Task<(FriendsOutcome Outcome, IReadOnlyList<FriendCandidate> Candidates)> SearchAsync(string text, CancellationToken cancellationToken = default)
    {
        var (outcome, value) = await ReadAsync<FriendCandidate[]>($"{Root}/search?q={Uri.EscapeDataString(text)}", cancellationToken);
        return (outcome, value ?? []);
    }

    public Task<FriendsOutcome> AddAsync(Guid playerId, CancellationToken cancellationToken = default) =>
        MutateAsync(HttpMethod.Post, playerId, cancellationToken);

    public Task<FriendsOutcome> RemoveAsync(Guid playerId, CancellationToken cancellationToken = default) =>
        MutateAsync(HttpMethod.Delete, playerId, cancellationToken);

    private async Task<(FriendsOutcome, T?)> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return (FriendsOutcome.Unavailable, default);
        if (!response.IsSuccessStatusCode) return (FriendsOutcome.Failed, default);
        return (FriendsOutcome.Ok, await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken));
    }

    private async Task<FriendsOutcome> MutateAsync(HttpMethod method, Guid playerId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (string.IsNullOrEmpty(antiforgeryToken) && !await RefreshAntiforgeryAsync(cancellationToken)) return FriendsOutcome.Failed;
            using var request = new HttpRequestMessage(method, $"{Root}/{playerId}");
            request.Headers.TryAddWithoutValidation("X-CSRF", antiforgeryToken);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest && attempt == 0 && await IsInvalidAntiforgeryAsync(response, cancellationToken))
            {
                antiforgeryToken = null;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound) return FriendsOutcome.Unavailable;
            return response.IsSuccessStatusCode ? FriendsOutcome.Ok : FriendsOutcome.Failed;
        }

        return FriendsOutcome.Failed;
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

    private sealed record AntiforgeryDocument(string? Token);
    private sealed record ErrorDocument(string? Error);
}
