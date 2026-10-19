using System.Net;
using System.Net.Sockets;
using Quesshi.Application.Media;

namespace Quesshi.Infrastructure.Media;

/// <summary>DNS resolver kept behind a contract so each fetch can check the concrete connection address.</summary>
public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>Rejects non-public destinations, including mapped IPv4 and cloud metadata addresses.</summary>
public static class MediaDestinationPolicy
{
    public static bool IsPermitted(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (TryExtractWellKnownNat64Address(address, out var embeddedIpv4)) address = embeddedIpv4;
        if (IPAddress.IsLoopback(address) || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || IsUniqueLocal(address)) return false;
            return true;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            0 or 10 or 127 or >= 224 => false,
            100 when bytes[1] is >= 64 and <= 127 => false,
            169 when bytes[1] == 254 => false,
            172 when bytes[1] is >= 16 and <= 31 => false,
            192 when bytes[1] is 0 or 168 => false,
            198 when bytes[1] is 18 or 19 => false,
            _ => true,
        };
    }

    private static bool IsUniqueLocal(IPAddress address) => (address.GetAddressBytes()[0] & 0xfe) == 0xfc;

    private static bool TryExtractWellKnownNat64Address(IPAddress address, out IPAddress embeddedIpv4)
    {
        embeddedIpv4 = IPAddress.None;
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] != 0x00 || bytes[1] != 0x64 || bytes[2] != 0xff || bytes[3] != 0x9b
            || bytes[4] != 0 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0
            || bytes[8] != 0 || bytes[9] != 0 || bytes[10] != 0 || bytes[11] != 0)
            return false;
        embeddedIpv4 = new IPAddress(bytes[12..]);
        return true;
    }
}

/// <summary>Streams a checked HTTP response into the immutable media store without auto-following redirects.</summary>
public sealed class MediaFetcher : IMediaFetcher
{
    private readonly IHostAddressResolver resolver;
    private readonly IMediaStore store;
    private readonly IMediaFetchTransport transport;
    private readonly TimeSpan timeout;

    public MediaFetcher(IHostAddressResolver resolver, IMediaStore store)
        : this(resolver, store, new CheckedMediaFetchTransport())
    {
    }

    public MediaFetcher(IHostAddressResolver resolver, IMediaStore store, IMediaFetchTransport transport)
        : this(resolver, store, transport, TimeSpan.FromSeconds(10))
    {
    }

    public MediaFetcher(
        IHostAddressResolver resolver,
        IMediaStore store,
        IMediaFetchTransport transport,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.resolver = resolver;
        this.store = store;
        this.transport = transport;
        this.timeout = timeout;
    }

    public Task<MediaFetchResult> FetchAsync(Guid tenantId, Uri url, CancellationToken cancellationToken = default) =>
        FetchAsync(tenantId, url, null, cancellationToken);

    public async Task<MediaFetchResult> FetchAsync(Guid tenantId, Uri url, string? attribution, CancellationToken cancellationToken = default)
    {
        if (!IsHttp(url)) return new(MediaFetchStatus.InvalidUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(this.timeout);
        try
        {
            var current = url;
            for (var redirects = 0; redirects <= 3; redirects++)
            {
                var address = await ResolvePublicAddressAsync(current, timeout.Token);
                if (address is null) return new(MediaFetchStatus.ForbiddenDestination);
                using var fetched = await transport.GetAsync(current, address, timeout.Token);
                var response = fetched.Response;
                if (IsRedirect(response.StatusCode))
                {
                    if (redirects == 3) return new(MediaFetchStatus.RedirectLimit);
                    if (response.Headers.Location is null || !Uri.TryCreate(current, response.Headers.Location, out var target) || !IsHttp(target))
                        return new(MediaFetchStatus.InvalidUrl);
                    current = target;
                    continue;
                }

                if (!response.IsSuccessStatusCode) return new(MediaFetchStatus.Failed);
                var declaredMime = NormalizeMime(response.Content.Headers.ContentType?.MediaType);
                if (!TryKindFor(declaredMime, out var expectedKind)) return new(MediaFetchStatus.UnsupportedMedia);
                var bytes = await ReadLimitedAsync(response, Limit(expectedKind), timeout.Token);
                if (bytes is null) return new(MediaFetchStatus.TooLarge);
                if (!MediaSniffer.TryInspect(bytes, out var actualKind, out var actualMime, out var meta)
                    || actualKind != expectedKind
                    || !string.Equals(actualMime, declaredMime, StringComparison.Ordinal))
                    return new(MediaFetchStatus.MismatchedMedia);
                return new(MediaFetchStatus.Success, await store.StoreAsync(tenantId, new MediaInput(actualKind, actualMime, bytes, meta, attribution), timeout.Token));
            }

            return new(MediaFetchStatus.RedirectLimit);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(MediaFetchStatus.Timeout);
        }
        catch (HttpRequestException)
        {
            return new(MediaFetchStatus.Failed);
        }
        catch (SocketException)
        {
            return new(MediaFetchStatus.Failed);
        }
    }

    public async Task<MediaFetchResult> ProbeAsync(Uri url, CancellationToken cancellationToken = default)
    {
        if (!IsHttp(url)) return new(MediaFetchStatus.InvalidUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(this.timeout);
        try
        {
            var current = url;
            for (var redirects = 0; redirects <= 3; redirects++)
            {
                var address = await ResolvePublicAddressAsync(current, timeout.Token);
                if (address is null) return new(MediaFetchStatus.ForbiddenDestination);
                using var fetched = await transport.GetAsync(current, address, timeout.Token);
                var response = fetched.Response;
                if (IsRedirect(response.StatusCode))
                {
                    if (redirects == 3) return new(MediaFetchStatus.RedirectLimit);
                    if (response.Headers.Location is null || !Uri.TryCreate(current, response.Headers.Location, out var target) || !IsHttp(target))
                        return new(MediaFetchStatus.InvalidUrl);
                    current = target;
                    continue;
                }

                if (!response.IsSuccessStatusCode) return new(MediaFetchStatus.Failed);
                var mime = NormalizeMime(response.Content.Headers.ContentType?.MediaType);
                return TryKindFor(mime, out var kind)
                    ? new(MediaFetchStatus.Success, Kind: kind)
                    : new(MediaFetchStatus.UnsupportedMedia);
            }
            return new(MediaFetchStatus.RedirectLimit);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(MediaFetchStatus.Timeout);
        }
        catch (HttpRequestException)
        {
            return new(MediaFetchStatus.Failed);
        }
        catch (SocketException)
        {
            return new(MediaFetchStatus.Failed);
        }
    }

    private async Task<IPAddress?> ResolvePublicAddressAsync(Uri uri, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(uri.Host, out var literal)
            ? new[] { literal }
            : await resolver.ResolveAsync(uri.DnsSafeHost, cancellationToken);
        return addresses.Count == 0 || addresses.Any(address => !MediaDestinationPolicy.IsPermitted(address))
            ? null
            : addresses[0];
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > limit) return null;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[65_536];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > limit) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    private static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(uri.Host);
    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;
    private static string NormalizeMime(string? mime) => mime?.Trim().ToLowerInvariant() ?? string.Empty;
    private static bool TryKindFor(string mime, out MediaKind kind)
    {
        kind = mime switch
        {
            "image/jpeg" or "image/png" or "image/webp" => MediaKind.Image,
            "audio/mpeg" or "audio/ogg" or "audio/wav" => MediaKind.Audio,
            "video/mp4" or "video/webm" => MediaKind.Video,
            _ => default,
        };
        return mime is "image/jpeg" or "image/png" or "image/webp"
            or "audio/mpeg" or "audio/ogg" or "audio/wav"
            or "video/mp4" or "video/webm";
    }

    private static long Limit(MediaKind kind) => kind switch
    {
        MediaKind.Image => 5 * 1024 * 1024,
        MediaKind.Audio => 10 * 1024 * 1024,
        MediaKind.Video => 25 * 1024 * 1024,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
