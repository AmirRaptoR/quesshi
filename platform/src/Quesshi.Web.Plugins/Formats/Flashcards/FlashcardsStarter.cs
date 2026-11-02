using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Web.Services;

namespace Quesshi.Web.Plugins.Formats.Flashcards;

/// <summary>Creates a flashcards session from a pack; a solo deck has no lobby, so the session starts running at once.</summary>
public sealed class FlashcardsStarter(ClientHttp http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string? _antiforgery;

    public sealed record Pack(string Id, string Title);

    /// <summary>The signed-in player's packs.</summary>
    public async Task<IReadOnlyList<Pack>> PacksAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<Pack>>("/api/packs", cancellationToken) ?? [];

    /// <summary>The number of cards in the pack.</summary>
    public async Task<int> CardCountAsync(string packId, CancellationToken cancellationToken = default) =>
        (await http.GetFromJsonAsync<List<JsonElement>>($"/api/packs/{Uri.EscapeDataString(packId)}/questions", cancellationToken))?.Count ?? 0;

    /// <summary>Creates the session; returns its id, or null with the server's stable error code.</summary>
    public async Task<(Guid? Id, string? Error)> CreateAsync(string packId, int count, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var body = new
        {
            id,
            format = FlashcardsClient.FormatId,
            content = new { source = "pack", parameters = new { packId }, count },
            rules = new { },
            capacity = 1,
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (string.IsNullOrEmpty(_antiforgery)) await RefreshAntiforgeryAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions") { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("X-CSRF", _antiforgery);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return (id, null);
            var error = await ReadErrorAsync(response, cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest && attempt == 0 && error == "invalid_antiforgery")
            {
                _antiforgery = null;
                continue;
            }

            return (null, error ?? "request_failed");
        }

        return (null, "request_failed");
    }

    private async Task RefreshAntiforgeryAsync(CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery"), cancellationToken);
        response.EnsureSuccessStatusCode();
        _antiforgery = (await response.Content.ReadFromJsonAsync<TokenDocument>(JsonOptions, cancellationToken))?.Token;
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return (await response.Content.ReadFromJsonAsync<ErrorDocument>(JsonOptions, cancellationToken))?.Error; }
        catch (JsonException) { return null; }
    }

    private sealed record TokenDocument(string? Token);
    private sealed record ErrorDocument(string? Error);
}
