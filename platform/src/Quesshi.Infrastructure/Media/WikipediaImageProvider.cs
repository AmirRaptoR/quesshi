using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quesshi.Application.Media;

namespace Quesshi.Infrastructure.Media;

/// <summary>Copies Wikipedia's curated lead-image path while retaining its licence attribution.</summary>
public sealed partial class WikipediaImageProvider(
    IOptions<WikipediaImageOptions> options,
    IHttpClientFactory http,
    IMediaStore store,
    ILogger<WikipediaImageProvider> logger) : ILeadImageProvider
{
    private const string Api = "https://en.wikipedia.org/w/api.php";
    private static readonly string[] AllowedLicences =
        ["cc0", "cc by", "cc by-sa", "cc-by", "cc-by-sa", "public domain", "pd-", "no restrictions"];

    public async Task<MediaAsset?> ProvideAsync(Guid tenantId, string subject, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        try
        {
            using var client = http.CreateClient(nameof(WikipediaImageProvider));
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.Value.UserAgent);
            var lead = await LeadImageAsync(client, subject, cancellationToken);
            if (lead is null) return null;
            var licence = await LicenceAsync(client, lead.Value.File, cancellationToken);
            if (licence is null || !IsRedistributable(licence.Value.Licence) || !IsSupportedMime(licence.Value.Mime)) return null;

            using var response = await client.GetAsync(lead.Value.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > options.Value.MaxBytes) return null;
            var bytes = await ReadLimitedAsync(response, options.Value.MaxBytes, cancellationToken);
            if (bytes is null || !MediaSniffer.TryInspect(bytes, out var kind, out var mime, out var meta)
                || kind != MediaKind.Image || !string.Equals(mime, licence.Value.Mime, StringComparison.OrdinalIgnoreCase)) return null;

            var attribution = $"{licence.Value.Artist}, {licence.Value.Licence}, via Wikimedia Commons — "
                + $"https://commons.wikimedia.org/wiki/File:{Uri.EscapeDataString(lead.Value.File)}";
            return await store.StoreAsync(tenantId, new MediaInput(kind, mime, bytes, meta, attribution), cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Could not source a Wikipedia lead image for {Subject}", subject);
            return null;
        }
    }

    private async Task<(string Url, string File)?> LeadImageAsync(HttpClient client, string subject, CancellationToken cancellationToken)
    {
        var url = $"{Api}?action=query&format=json&redirects=1&prop=pageimages&piprop=thumbnail|name"
            + $"&pithumbsize={options.Value.Width}&titles={Uri.EscapeDataString(subject)}";
        using var document = JsonDocument.Parse(await client.GetStringAsync(url, cancellationToken));
        if (!document.RootElement.TryGetProperty("query", out var query)) return null;
        foreach (var page in query.GetProperty("pages").EnumerateObject())
        {
            if (!page.Value.TryGetProperty("thumbnail", out var thumbnail) || !page.Value.TryGetProperty("pageimage", out var name)) continue;
            var source = thumbnail.GetProperty("source").GetString();
            var file = name.GetString();
            if (source is not null && file is not null) return (source, file);
        }
        return null;
    }

    private static async Task<(string Licence, string Artist, string Mime)?> LicenceAsync(HttpClient client, string fileName, CancellationToken cancellationToken)
    {
        var url = $"{Api}?action=query&format=json&prop=imageinfo&iiprop=extmetadata|mime&titles=File:{Uri.EscapeDataString(fileName)}";
        using var document = JsonDocument.Parse(await client.GetStringAsync(url, cancellationToken));
        if (!document.RootElement.TryGetProperty("query", out var query)) return null;
        foreach (var page in query.GetProperty("pages").EnumerateObject())
        {
            if (!page.Value.TryGetProperty("imageinfo", out var info) || info.GetArrayLength() == 0) continue;
            var value = info[0];
            if (!value.TryGetProperty("extmetadata", out var metadata)) continue;
            return (MetadataValue(metadata, "LicenseShortName") ?? "", Plain(MetadataValue(metadata, "Artist") ?? "Unknown"),
                value.TryGetProperty("mime", out var mime) ? mime.GetString() ?? "" : "");
        }
        return null;
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[65_536];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > limit) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool IsRedistributable(string licence) => AllowedLicences.Any(allowed => licence.Contains(allowed, StringComparison.OrdinalIgnoreCase));
    private static bool IsSupportedMime(string mime) => mime is "image/jpeg" or "image/png" or "image/webp";
    private static string? MetadataValue(JsonElement metadata, string key) => metadata.TryGetProperty(key, out var node) && node.TryGetProperty("value", out var value) ? value.GetString() : null;
    private static string Plain(string html) => string.Join(' ', System.Net.WebUtility.HtmlDecode(Tags().Replace(html, " ")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
