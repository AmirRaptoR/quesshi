using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quesshi.Application.Media;
using Quesshi.Infrastructure.Media;

namespace Quesshi.Infrastructure.Tests;

public sealed class WikipediaImageProviderTests
{
    [Fact]
    public async Task Accepted_wikipedia_lead_image_is_stored_with_redistributable_attribution()
    {
        var store = new CapturingStore();
        var provider = Provider(store);

        var asset = await provider.ProvideAsync(Guid.NewGuid(), "Example");

        Assert.NotNull(asset);
        Assert.Equal(MediaKind.Image, store.Input!.Kind);
        Assert.Equal("image/png", store.Input.Mime);
        Assert.Equal(2, store.Input.Meta.GetProperty("width").GetInt32());
        Assert.Contains("Alice", store.Input.Attribution);
        Assert.Contains("CC BY-SA", store.Input.Attribution);
        Assert.Contains("https://commons.wikimedia.org/wiki/File:Example.png", store.Input.Attribution);
    }

    [Fact]
    public async Task Non_redistributable_wikipedia_lead_image_is_not_stored()
    {
        var store = new CapturingStore();

        var asset = await Provider(store, licence: "Fair use").ProvideAsync(Guid.NewGuid(), "Example");

        Assert.Null(asset);
        Assert.Equal(0, store.StoreCalls);
    }

    [Fact]
    public async Task Unsupported_wikipedia_lead_image_mime_is_not_stored()
    {
        var store = new CapturingStore();

        var asset = await Provider(store, mime: "image/gif").ProvideAsync(Guid.NewGuid(), "Example");

        Assert.Null(asset);
        Assert.Equal(0, store.StoreCalls);
    }

    private static WikipediaImageProvider Provider(CapturingStore store, string licence = "CC BY-SA 4.0", string mime = "image/png") => new(
        Options.Create(new WikipediaImageOptions()),
        new ClientFactory(licence, mime),
        store,
        NullLogger<WikipediaImageProvider>.Instance);

    private sealed class ClientFactory(string licence, string mime) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new WikipediaHandler(licence, mime));
    }

    private sealed class WikipediaHandler(string licence, string mime) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = request.RequestUri!.Host == "images.example"
                ? Image()
                : request.RequestUri.Query.Contains("pageimages", StringComparison.Ordinal)
                    ? Json("""{"query":{"pages":{"1":{"thumbnail":{"source":"https://images.example/example.png"},"pageimage":"Example.png"}}}}""")
                    : Json(JsonSerializer.Serialize(new
                    {
                        query = new
                        {
                            pages = new Dictionary<string, object>
                            {
                                ["1"] = new
                                {
                                    imageinfo = new[]
                                    {
                                        new
                                        {
                                            extmetadata = new
                                            {
                                                LicenseShortName = new { value = licence },
                                                Artist = new { value = "<b>Alice</b>" },
                                            },
                                            mime,
                                        },
                                    },
                                },
                            },
                        },
                    }));
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Image() => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 2, 0, 0, 0, 1]),
        };

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value),
        };
    }

    private sealed class CapturingStore : IMediaStore
    {
        public MediaInput? Input { get; private set; }
        public int StoreCalls { get; private set; }

        public Task<MediaAsset> StoreAsync(Guid tenantId, MediaInput input, CancellationToken cancellationToken = default)
        {
            StoreCalls++;
            Input = input;
            return Task.FromResult(new MediaAsset(Guid.NewGuid(), "a".PadLeft(64, 'a'), input.Kind, input.Mime, input.Bytes.Length, input.Meta, input.Attribution, DateTimeOffset.UnixEpoch));
        }

        public Task<MediaAsset?> FindByHashAsync(Guid tenantId, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(Guid tenantId, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
