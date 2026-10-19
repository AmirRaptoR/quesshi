using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Quesshi.Application.Media;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Media;

/// <summary>Stores tenant media under a content hash without permitting one write to replace another.</summary>
public sealed class LocalMediaStore(ITenantDb database, IConfiguration configuration) : IMediaStore
{
    private readonly string _root = configuration["MEDIA_DIR"]
        ?? Path.Combine(AppContext.BaseDirectory, "media");

    public async Task<MediaAsset> StoreAsync(Guid tenantId, MediaInput input, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(input.Kind) || string.IsNullOrWhiteSpace(input.Mime)) throw new ArgumentException("Media kind and MIME type are required.", nameof(input));
        var bytes = input.Bytes;
        var hash = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        var existing = await FindByHashAsync(tenantId, hash, cancellationToken);
        if (existing is not null) return existing;

        var path = PathFor(tenantId, hash);
        await WriteOnceAsync(path, bytes, cancellationToken);
        return await database.InTenantAsync(tenantId, async transaction =>
        {
            var stored = await transaction.QuerySingleOrDefaultAsync<PersistedAsset>("""
                insert into media_assets (tenant_id, id, kind, storage_key, mime, bytes, meta, attribution)
                values (current_setting('app.tenant_id')::uuid, @id, @kind, @hash, @mime, @bytes, @meta, @attribution)
                on conflict (tenant_id, storage_key) do nothing
                returning id as Id, storage_key as StorageKey, kind as Kind, mime as Mime, bytes as Bytes,
                          meta::text as MetaJson, attribution as Attribution, created_at as CreatedAt
                """, new
            {
                id = Guid.NewGuid(),
                kind = KindValue(input.Kind),
                hash,
                mime = input.Mime,
                bytes = bytes.Length,
                meta = new Jsonb<JsonElement>(input.Meta, MediaJsonContext.Default.JsonElement),
                attribution = TrimOrNull(input.Attribution),
            }, cancellationToken);
            if (stored is not null) return ToAsset(stored);
            var duplicate = await transaction.QuerySingleOrDefaultAsync<PersistedAsset>("""
                select id as Id, storage_key as StorageKey, kind as Kind, mime as Mime, bytes as Bytes,
                       meta::text as MetaJson, attribution as Attribution, created_at as CreatedAt
                from media_assets where storage_key = @hash
                """, new { hash }, cancellationToken);
            return duplicate is null
                ? throw new InvalidOperationException("A media asset was not available after its immutable file was written.")
                : ToAsset(duplicate);
        }, cancellationToken: cancellationToken);
    }

    public async Task<MediaAsset?> FindByHashAsync(Guid tenantId, string hash, CancellationToken cancellationToken = default)
    {
        if (!IsHash(hash)) return null;
        return await database.InTenantAsync(tenantId, async transaction =>
        {
            var asset = await transaction.QuerySingleOrDefaultAsync<PersistedAsset>("""
                select id as Id, storage_key as StorageKey, kind as Kind, mime as Mime, bytes as Bytes,
                       meta::text as MetaJson, attribution as Attribution, created_at as CreatedAt
                from media_assets where storage_key = @hash
                """, new { hash }, cancellationToken);
            return asset is null ? null : ToAsset(asset);
        }, cancellationToken: cancellationToken, readOnly: true);
    }

    public async Task<Stream?> OpenReadAsync(Guid tenantId, string hash, CancellationToken cancellationToken = default)
    {
        var asset = await FindByHashAsync(tenantId, hash, cancellationToken);
        if (asset is null) return null;
        var path = PathFor(tenantId, asset.StorageKey);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
    {
        var hash = await database.InTenantAsync(tenantId, transaction => transaction.QuerySingleOrDefaultAsync<string>("""
            delete from media_assets
            where id = @id
              and not exists (select 1 from questions where media_id = @id)
              and not exists (select 1 from session_items where media_id = @id)
            returning storage_key
            """, new { id }, cancellationToken), cancellationToken: cancellationToken);
        if (hash is null) return false;
        TryDelete(PathFor(tenantId, hash));
        return true;
    }

    private string PathFor(Guid tenantId, string hash) => Path.Combine(_root, tenantId.ToString("N"), hash[..2], hash);

    private static async Task<bool> WriteOnceAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            try
            {
                File.Move(temporary, path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static MediaAsset ToAsset(PersistedAsset asset) => new(
        asset.Id, asset.StorageKey, ParseKind(asset.Kind), asset.Mime, asset.Bytes,
        JsonDocument.Parse(asset.MetaJson).RootElement.Clone(), asset.Attribution, asset.CreatedAt);

    private static MediaKind ParseKind(string kind) => Enum.Parse<MediaKind>(kind, true);
    private static string KindValue(MediaKind kind) => kind.ToString().ToLowerInvariant();
    private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool IsHash(string hash) => hash.Length == 64 && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } }

    private sealed record PersistedAsset(Guid Id, string StorageKey, string Kind, string Mime, long Bytes, string MetaJson, string? Attribution, DateTimeOffset CreatedAt);
}

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class MediaJsonContext : JsonSerializerContext;
