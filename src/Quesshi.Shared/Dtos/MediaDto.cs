namespace Quesshi.Shared;

/// <summary>
/// Kind is "image", "audio" or "video"; the url is relative to the site root. Attribution is the
/// credit line a licence requires, and must be shown wherever the media is.
/// </summary>
public sealed record MediaDto(string Kind, string Url, string? Attribution = null);
