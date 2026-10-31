using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class StreamClientTests
{
    [Theory]
    [InlineData(0, 0, 250)]
    [InlineData(0, 1, 500)]
    [InlineData(3, 1, 4000)]
    [InlineData(40, 1, 30000)]
    [InlineData(40, 0, 15000)]
    public void Retry_delay_grows_exponentially_with_jitter_and_stays_capped(int attempt, double jitter, double milliseconds) =>
        Assert.Equal(milliseconds, StreamClient.RetryDelay(attempt, jitter).TotalMilliseconds);

    [Fact]
    public void Stream_path_carries_the_watched_session_only_when_there_is_one()
    {
        var session = Guid.NewGuid();
        Assert.Equal("/api/stream", StreamClient.PathFor(null));
        Assert.Equal($"/api/stream?session={session}", StreamClient.PathFor(session));
    }

    [Fact]
    public async Task Typed_frames_are_raised_with_their_kind_and_data_and_unknown_types_are_ignored()
    {
        var handler = new SequenceHandler(Sse(
            "event: view\ndata: {\"v\":1}\n\nevent: inbox\ndata: i\n\nevent: config\ndata: c\n\nevent: effects\ndata: e\n\nevent: mystery\ndata: x\n\nevent: closed\ndata: {}\n\n"));
        var received = await RunUntilAsync(handler, notifications => notifications.Any(n => n.Kind == StreamNotificationKind.Closed));

        Assert.Equal(
            [
                new StreamNotification(StreamNotificationKind.View, "{\"v\":1}"),
                new StreamNotification(StreamNotificationKind.Inbox, "i"),
                new StreamNotification(StreamNotificationKind.Config, "c"),
                new StreamNotification(StreamNotificationKind.Effects, "e"),
                new StreamNotification(StreamNotificationKind.Closed, "{}")
            ],
            received);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task After_a_dropped_response_the_client_reconnects_and_receives_the_current_snapshot()
    {
        var handler = new SequenceHandler(
            Sse("event: view\ndata: {\"version\":1}\n\n"),
            Sse("event: view\ndata: {\"version\":2}\n\n", keepOpen: true));
        var received = await RunUntilAsync(handler, notifications => notifications.Count >= 2);

        Assert.Equal(["{\"version\":1}", "{\"version\":2}"], received.Select(n => n.Data));
        Assert.All(received, n => Assert.Equal(StreamNotificationKind.View, n.Kind));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_success_response_that_is_not_an_event_stream_is_rejected_before_parsing()
    {
        var handler = new SequenceHandler(
            Respond("application/json", "event: view\ndata: {\"version\":99}\n\nevent: closed\ndata: {}\n\n"),
            Sse("event: view\ndata: {\"version\":1}\n\n", keepOpen: true));
        var received = await RunUntilAsync(handler, notifications => notifications.Count >= 1);

        Assert.Equal([new StreamNotification(StreamNotificationKind.View, "{\"version\":1}")], received);
        Assert.Equal(2, handler.Calls);
    }

    private static async Task<List<StreamNotification>> RunUntilAsync(SequenceHandler handler, Func<List<StreamNotification>, bool> done)
    {
        var http = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        await using var client = new StreamClient(http);
        var received = new List<StreamNotification>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Received += notification =>
        {
            lock (received)
            {
                received.Add(notification);
                if (done(received)) complete.TrySetResult();
            }
        };
        await client.SyncAsync(true, null);
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (received) return [.. received];
    }

    private static Func<HttpResponseMessage> Sse(string body, bool keepOpen = false) =>
        () =>
        {
            Stream stream = keepOpen ? new OpenEndedStream(Encoding.UTF8.GetBytes(body)) : new MemoryStream(Encoding.UTF8.GetBytes(body));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        };

    private static Func<HttpResponseMessage> Respond(string contentType, string body) =>
        () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private sealed class SequenceHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref calls) - 1;
            return Task.FromResult(responses[Math.Min(index, responses.Length - 1)]());
        }
    }

    /// <summary>Serves its bytes, then blocks like a live connection until cancelled.</summary>
    private sealed class OpenEndedStream(byte[] bytes) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position < bytes.Length)
            {
                var count = Math.Min(buffer.Length, bytes.Length - position);
                bytes.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }
}
