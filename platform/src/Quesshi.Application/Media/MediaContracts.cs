using System.Text.Json;

namespace Quesshi.Application.Media;

/// <summary>The verified family of a content-addressed media asset.</summary>
public enum MediaKind
{
    Image,
    Audio,
    Video,
}

/// <summary>An immutable tenant-owned media asset and the metadata recorded at ingestion.</summary>
public sealed record MediaAsset(
    Guid Id,
    string StorageKey,
    MediaKind Kind,
    string Mime,
    long Bytes,
    JsonElement Meta,
    string? Attribution,
    DateTimeOffset CreatedAt);

/// <summary>Validated content that is ready to be committed to the media store.</summary>
public sealed record MediaInput(MediaKind Kind, string Mime, ReadOnlyMemory<byte> Bytes, JsonElement Meta, string? Attribution = null);

/// <summary>Stores immutable bytes per tenant and exposes only tenant-scoped asset lookup.</summary>
public interface IMediaStore
{
    Task<MediaAsset> StoreAsync(Guid tenantId, MediaInput input, CancellationToken cancellationToken = default);

    Task<MediaAsset?> FindByHashAsync(Guid tenantId, string hash, CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(Guid tenantId, string hash, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Finds a redistributable Wikipedia lead image and saves it as a tenant asset.</summary>
public interface ILeadImageProvider
{
    Task<MediaAsset?> ProvideAsync(Guid tenantId, string subject, CancellationToken cancellationToken = default);
}
