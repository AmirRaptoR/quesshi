using System.Text.Json;

namespace Quesshi.Web.Admin;

public sealed record AdminMediaAsset(
    Guid Id,
    string Hash,
    string Kind,
    string Mime,
    long Bytes,
    JsonElement Meta,
    string? Attribution,
    string Url);

public sealed record AdminMediaFetchRequest(string Url, string? Attribution);

internal static class AdminMediaText
{
    public const string Title = "admin.media.title";
    public const string Attribution = "admin.media.attribution";
    public const string Upload = "admin.media.upload";
    public const string Url = "admin.media.url";
    public const string Fetch = "admin.media.fetch";
    public const string Selected = "admin.media.selected";
    public const string Error = "admin.media.error";

    public static string ForError(string? code) => string.IsNullOrWhiteSpace(code) ? Error : $"admin.media.{code}";
}
