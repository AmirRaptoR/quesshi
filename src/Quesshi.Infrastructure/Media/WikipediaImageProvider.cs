using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Media;

/// <summary>
/// Takes the lead image of a Wikipedia article and stores it locally with its attribution.
///
/// The lead image is used rather than a free-text image search because it is curated to depict the
/// article's subject — which is the whole basis for trusting that the picture shows the answer.
/// </summary>
public sealed partial class WikipediaImageProvider(
    WikipediaImageOptions options,
    IHttpClientFactory http,
    IIdFactory ids,
    ILogger<WikipediaImageProvider> logger) : IQuestionImageProvider
{
    private const string Api = "https://en.wikipedia.org/w/api.php";

    /// <summary>
    /// Wikipedia also hosts non-free "fair use" material — album covers, film posters, logos.
    /// Those may not be redistributed, so only these licences are accepted.
    /// </summary>
    private static readonly string[] AllowedLicences =
        ["cc0", "cc by", "cc by-sa", "cc-by", "cc-by-sa", "public domain", "pd-", "no restrictions"];

    private static readonly string[] AllowedTypes = ["image/jpeg", "image/png", "image/webp"];

    public bool IsConfigured => true;   // no key needed, which is rather the point

    public async Task<MediaRef?> ProvideAsync(string subject, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;

        try
        {
            using var client = http.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(30);

            var lead = await LeadImageAsync(client, subject, ct);
            if (lead is null)
            {
                logger.LogDebug("No lead image for {Subject}", subject);
                return null;
            }

            var (thumbnailUrl, fileName) = lead.Value;
            var licence = await LicenceAsync(client, fileName, ct);

            if (licence is null || !IsRedistributable(licence.Value.Licence))
            {
                logger.LogInformation("Skipping {Subject}: licence {Licence} is not redistributable",
                    subject, licence?.Licence ?? "unknown");
                return null;
            }

            if (!AllowedTypes.Contains(licence.Value.Mime, StringComparer.OrdinalIgnoreCase))
            {
                logger.LogInformation("Skipping {Subject}: {Mime} is not an image type we serve", subject, licence.Value.Mime);
                return null;
            }

            var stored = await DownloadAsync(client, thumbnailUrl, ct);
            if (stored is null) return null;

            var credit = $"{licence.Value.Artist}, {licence.Value.Licence}, via Wikimedia Commons — " +
                         $"https://commons.wikimedia.org/wiki/File:{Uri.EscapeDataString(fileName)}";

            return new MediaRef(MediaKind.Image, stored, credit);
        }
        catch (Exception ex)
        {
            // No picture is a missing media question, never a failed batch.
            logger.LogWarning(ex, "Could not source an image for {Subject}", subject);
            return null;
        }
    }

    private async Task<(string Url, string File)?> LeadImageAsync(HttpClient client, string subject, CancellationToken ct)
    {
        var url = $"{Api}?action=query&format=json&redirects=1&prop=pageimages&piprop=thumbnail|name" +
                  $"&pithumbsize={options.Width}&titles={Uri.EscapeDataString(subject)}";

        using var doc = JsonDocument.Parse(await client.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("query", out var query)) return null;

        foreach (var page in query.GetProperty("pages").EnumerateObject())
        {
            if (!page.Value.TryGetProperty("thumbnail", out var thumb)) continue;
            if (!page.Value.TryGetProperty("pageimage", out var name)) continue;

            return (thumb.GetProperty("source").GetString()!, name.GetString()!);
        }

        return null;
    }

    private async Task<(string Licence, string Artist, string Mime)?> LicenceAsync(HttpClient client, string fileName, CancellationToken ct)
    {
        var url = $"{Api}?action=query&format=json&prop=imageinfo&iiprop=extmetadata|mime" +
                  $"&titles=File:{Uri.EscapeDataString(fileName)}";

        using var doc = JsonDocument.Parse(await client.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("query", out var query)) return null;

        foreach (var page in query.GetProperty("pages").EnumerateObject())
        {
            if (!page.Value.TryGetProperty("imageinfo", out var info) || info.GetArrayLength() == 0) continue;

            var first = info[0];
            var meta = first.GetProperty("extmetadata");

            return (Value(meta, "LicenseShortName") ?? "", Plain(Value(meta, "Artist") ?? "Unknown"),
                    first.TryGetProperty("mime", out var mime) ? mime.GetString() ?? "" : "");
        }

        return null;
    }

    private async Task<string?> DownloadAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        if (response.Content.Headers.ContentLength > options.MaxBytes)
        {
            logger.LogInformation("Skipping an image of {Bytes} bytes", response.Content.Headers.ContentLength);
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0 || bytes.Length > options.MaxBytes) return null;

        var extension = response.Content.Headers.ContentType?.MediaType switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg"
        };

        Directory.CreateDirectory(options.StorageRoot);
        var name = ids.NewId() + extension;
        await File.WriteAllBytesAsync(Path.Combine(options.StorageRoot, name), bytes, ct);

        return $"{options.PublicPath.TrimEnd('/')}/{name}";
    }

    private static bool IsRedistributable(string licence)
    {
        var folded = licence.ToLowerInvariant();
        return AllowedLicences.Any(allowed => folded.Contains(allowed, StringComparison.Ordinal));
    }

    private static string? Value(JsonElement meta, string key)
        => meta.TryGetProperty(key, out var node) && node.TryGetProperty("value", out var value) ? value.GetString() : null;

    /// <summary>The metadata arrives as HTML; a credit line should be text.</summary>
    private static string Plain(string html)
    {
        var text = Tags().Replace(html, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
