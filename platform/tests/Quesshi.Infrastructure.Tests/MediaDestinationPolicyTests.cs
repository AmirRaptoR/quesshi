using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Quesshi.Application.Media;
using Quesshi.Infrastructure.Media;

namespace Quesshi.Infrastructure.Tests;

public sealed class MediaDestinationPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("0.1.2.3")]
    [InlineData("100.100.100.200")]
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    public void Private_and_special_destinations_are_refused(string value)
    {
        Assert.False(MediaDestinationPolicy.IsPermitted(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_destinations_are_permitted(string value)
    {
        Assert.True(MediaDestinationPolicy.IsPermitted(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("http://127.0.0.1/media")]
    [InlineData("http://10.0.0.1/media")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/media")]
    [InlineData("http://[fd00::1]/media")]
    [InlineData("http://[fe80::1]/media")]
    [InlineData("http://[::ffff:10.0.0.1]/media")]
    public async Task Fetcher_refuses_direct_forbidden_destinations_before_connecting(string url)
    {
        var resolver = new FixedResolver(IPAddress.Parse("1.1.1.1"));
        var result = await new MediaFetcher(resolver, new NeverStore()).FetchAsync(Guid.NewGuid(), new Uri(url));

        Assert.Equal(MediaFetchStatus.ForbiddenDestination, result.Status);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Fetcher_refuses_a_hostname_that_resolves_to_a_private_address()
    {
        var resolver = new FixedResolver(IPAddress.Parse("10.0.0.1"));
        var result = await new MediaFetcher(resolver, new NeverStore()).FetchAsync(Guid.NewGuid(), new Uri("https://media.example/test"));

        Assert.Equal(MediaFetchStatus.ForbiddenDestination, result.Status);
        Assert.Equal(1, resolver.Calls);
    }

    [Theory]
    [InlineData("ftp://media.example/image")]
    [InlineData("file:///tmp/image")]
    [InlineData("media.example/image")]
    public async Task Fetcher_rejects_non_http_urls_without_resolving_or_connecting(string url)
    {
        var resolver = new FixedResolver(IPAddress.Parse("1.1.1.1"));
        var transport = new ScriptedTransport();
        var result = await new MediaFetcher(resolver, new NeverStore(), transport)
            .FetchAsync(Guid.NewGuid(), new Uri(url, UriKind.RelativeOrAbsolute));

        Assert.Equal(MediaFetchStatus.InvalidUrl, result.Status);
        Assert.Equal(0, resolver.Calls);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Checked_transport_connects_to_the_verified_address_instead_of_resolving_the_hostname()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var served = ServeOnceAsync(listener, cancellation.Token);

        using var response = await new CheckedMediaFetchTransport().GetAsync(
            new Uri($"http://media.example.test:{port}/asset"), IPAddress.Loopback, cancellation.Token);
        var request = await served;

        Assert.Equal(HttpStatusCode.OK, response.Response.StatusCode);
        Assert.Contains($"Host: media.example.test:{port}", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fetcher_rechecks_every_redirect_before_opening_its_connection()
    {
        var resolver = new SequencedResolver(IPAddress.Parse("1.1.1.1"), IPAddress.Parse("10.0.0.1"));
        var transport = new ScriptedTransport(Redirect("http://private.example/image"));
        var result = await new MediaFetcher(resolver, new NeverStore(), transport)
            .FetchAsync(Guid.NewGuid(), new Uri("https://public.example/image"));

        Assert.Equal(MediaFetchStatus.ForbiddenDestination, result.Status);
        Assert.Equal(2, resolver.Calls);
        Assert.Single(transport.Calls);
        Assert.Equal(IPAddress.Parse("1.1.1.1"), transport.Calls[0].Address);
    }

    [Fact]
    public async Task Fetcher_stops_after_three_redirects()
    {
        var resolver = new FixedResolver(IPAddress.Parse("1.1.1.1"));
        var transport = new ScriptedTransport(
            Redirect("/one"), Redirect("/two"), Redirect("/three"), Redirect("/four"));
        var result = await new MediaFetcher(resolver, new NeverStore(), transport)
            .FetchAsync(Guid.NewGuid(), new Uri("https://public.example/start"));

        Assert.Equal(MediaFetchStatus.RedirectLimit, result.Status);
        Assert.Equal(4, resolver.Calls);
        Assert.Equal(4, transport.Calls.Count);
    }

    [Fact]
    public async Task Fetcher_uses_verified_response_type_and_does_not_store_mismatched_content()
    {
        var resolver = new FixedResolver(IPAddress.Parse("1.1.1.1"));
        var transport = new ScriptedTransport(Media("image/png", "not a png"u8.ToArray()));
        var result = await new MediaFetcher(resolver, new NeverStore(), transport)
            .FetchAsync(Guid.NewGuid(), new Uri("https://public.example/image"));

        Assert.Equal(MediaFetchStatus.MismatchedMedia, result.Status);
    }

    [Fact]
    public async Task Fetcher_rejects_an_oversized_stream_before_storing_it()
    {
        var resolver = new FixedResolver(IPAddress.Parse("1.1.1.1"));
        var transport = new ScriptedTransport(Media("image/png", "small"u8.ToArray(), 5 * 1024 * 1024 + 1));
        var result = await new MediaFetcher(resolver, new NeverStore(), transport)
            .FetchAsync(Guid.NewGuid(), new Uri("https://public.example/image"));

        Assert.Equal(MediaFetchStatus.TooLarge, result.Status);
    }

    [Fact]
    public async Task Fetcher_rejects_a_lengthless_audio_stream_over_its_kind_limit_without_storing()
    {
        var resolver = new FixedResolver(IPAddress.Parse("1.1.1.1"));
        var store = new NeverStore();
        var transport = new ScriptedTransport(Media("audio/wav", new byte[10 * 1024 * 1024 + 1], lengthless: true));
        var result = await new MediaFetcher(resolver, store, transport)
            .FetchAsync(Guid.NewGuid(), new Uri("https://public.example/audio"));

        Assert.Equal(MediaFetchStatus.TooLarge, result.Status);
        Assert.Equal(0, store.StoreCalls);
    }

    [Fact]
    public async Task Fetcher_applies_its_total_timeout_without_storing_an_asset()
    {
        var resolver = new FixedResolver(IPAddress.Parse("1.1.1.1"));
        var result = await new MediaFetcher(resolver, new NeverStore(), new WaitingTransport(), TimeSpan.FromMilliseconds(20))
            .FetchAsync(Guid.NewGuid(), new Uri("https://public.example/image"));

        Assert.Equal(MediaFetchStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task Probe_checks_media_headers_without_downloading_or_storing_the_body()
    {
        var resolver = new FixedResolver(IPAddress.Parse("1.1.1.1"));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ThrowingContent() };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var store = new NeverStore();
        var result = await new MediaFetcher(resolver, store, new ScriptedTransport(response))
            .ProbeAsync(new Uri("https://public.example/image"));

        Assert.Equal((MediaFetchStatus.Success, (MediaKind?)MediaKind.Image, 0), (result.Status, result.Kind, store.StoreCalls));
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) },
    };

    private static HttpResponseMessage Media(string mime, byte[] bytes, long? length = null, bool lengthless = false)
    {
        HttpContent content = lengthless ? new LengthlessContent(bytes) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        if (length is not null) content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static async Task<string> ServeOnceAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(response, cancellationToken);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private sealed class FixedResolver(params IPAddress[] addresses) : IHostAddressResolver
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<IPAddress>>(addresses);
        }
    }

    private sealed class SequencedResolver(params IPAddress[] addresses) : IHostAddressResolver
    {
        private int next;

        public int Calls { get; private set; }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<IPAddress>>([addresses[next++]]);
        }
    }

    private sealed class ScriptedTransport(params HttpResponseMessage[] responses) : IMediaFetchTransport
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<(Uri Uri, IPAddress Address)> Calls { get; } = [];

        public Task<MediaFetchResponse> GetAsync(Uri uri, IPAddress address, CancellationToken cancellationToken = default)
        {
            Calls.Add((uri, address));
            return Task.FromResult(new MediaFetchResponse(new HttpClient(), responses.Dequeue()));
        }
    }

    private sealed class NeverStore : IMediaStore
    {
        public int StoreCalls { get; private set; }

        public Task<MediaAsset> StoreAsync(Guid tenantId, MediaInput input, CancellationToken cancellationToken = default)
        {
            StoreCalls++;
            throw new InvalidOperationException("Forbidden requests cannot store media.");
        }
        public Task<MediaAsset?> FindByHashAsync(Guid tenantId, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MediaAsset?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(Guid tenantId, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class WaitingTransport : IMediaFetchTransport
    {
        public async Task<MediaFetchResponse> GetAsync(Uri uri, IPAddress address, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null!;
        }
    }

    private sealed class LengthlessContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => stream.WriteAsync(bytes, cancellationToken).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("A media probe must not read a response body.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
