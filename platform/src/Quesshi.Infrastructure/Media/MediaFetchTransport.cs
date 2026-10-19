using System.Net;
using System.Net.Sockets;

namespace Quesshi.Infrastructure.Media;

/// <summary>Opens one HTTP request at an already-checked address.</summary>
public interface IMediaFetchTransport
{
    Task<MediaFetchResponse> GetAsync(Uri uri, IPAddress address, CancellationToken cancellationToken = default);
}

/// <summary>Owns the client for a streamed response.</summary>
public sealed class MediaFetchResponse(HttpClient client, HttpResponseMessage response) : IDisposable
{
    public HttpResponseMessage Response { get; } = response;

    public void Dispose()
    {
        Response.Dispose();
        client.Dispose();
    }
}

/// <summary>HTTP transport which connects to the address approved by <see cref="MediaDestinationPolicy"/>.</summary>
public sealed class CheckedMediaFetchTransport : IMediaFetchTransport
{
    public async Task<MediaFetchResponse> GetAsync(Uri uri, IPAddress address, CancellationToken cancellationToken = default)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, uri.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        var client = new HttpClient(handler, disposeHandler: true);
        try
        {
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return new MediaFetchResponse(client, response);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
