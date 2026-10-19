using System.Net;

namespace Quesshi.Application.Media;

/// <summary>Resolves a host before a media fetcher opens a network connection.</summary>
public interface IHostAddressResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

public enum MediaFetchStatus
{
    Success,
    InvalidUrl,
    ForbiddenDestination,
    RedirectLimit,
    TooLarge,
    UnsupportedMedia,
    MismatchedMedia,
    Timeout,
    Failed,
}

public sealed record MediaFetchResult(MediaFetchStatus Status, MediaAsset? Asset = null, MediaKind? Kind = null);

/// <summary>Fetches remote media only after every connection target has passed destination policy.</summary>
public interface IMediaFetcher
{
    Task<MediaFetchResult> FetchAsync(Guid tenantId, Uri url, CancellationToken cancellationToken = default);

    /// <summary>Fetches media and records the attribution supplied by an importer.</summary>
    Task<MediaFetchResult> FetchAsync(Guid tenantId, Uri url, string? attribution, CancellationToken cancellationToken = default) =>
        FetchAsync(tenantId, url, cancellationToken);

    /// <summary>Checks a remote media URL without reading its body or creating an asset.</summary>
    Task<MediaFetchResult> ProbeAsync(Uri url, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MediaFetchResult(MediaFetchStatus.Failed));
}
