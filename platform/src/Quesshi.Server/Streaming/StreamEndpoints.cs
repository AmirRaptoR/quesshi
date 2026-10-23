using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Streaming;

internal static class StreamEndpoints
{
    private const int MaximumFrameBytes = 64 * 1024;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    public static IEndpointRouteBuilder MapStream(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/stream", StreamAsync);
        return endpoints;
    }

    internal static async Task StreamAsync(HttpContext context, [FromServices] StreamHub hub, [FromServices] ILogger<StreamHub> logger, Guid? session,
        CancellationToken cancellationToken)
    {
        var player = Player(context);
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (player is null || tenant is null || player.TenantId != tenant.Id)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (session is { } watched) context.Items["session_id"] = watched;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.Headers["Content-Encoding"] = "identity";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var sessionKey = context.Items.TryGetValue(typeof(PlayerStreamSession), out var streamSession)
            ? (streamSession as PlayerStreamSession)?.Key
            : null;
        var subscription = hub.Subscribe(tenant.Id, player.PlayerId.ToString(), session, sessionKey, player.IsGuest);
        try
        {
            await context.Response.StartAsync(cancellationToken);
            await hub.RenderInitialAsync(subscription, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var ready = subscription.Messages.WaitToReadAsync(cancellationToken).AsTask();
                var idle = Task.Delay(KeepAlive, cancellationToken);
                if (await Task.WhenAny(ready, idle) == idle)
                {
                    await WriteAndFlushAsync(context, ": keep-alive\n\n", cancellationToken);
                    continue;
                }
                if (!await ready) return;
                while (subscription.Messages.TryRead(out var message))
                {
                    if (!subscription.MarkSent(message)) continue;
                    var frame = Frame(message);
                    if (Encoding.UTF8.GetByteCount(frame) > MaximumFrameBytes)
                    {
                        logger.LogWarning("Stream frame for session {SessionId} exceeds the 64KB limit", session);
                        await WriteAndFlushAsync(context, Frame(new StreamMessage(StreamMessageKind.Gone, 0, Reason: "too_large")), cancellationToken);
                        return;
                    }
                    await WriteAndFlushAsync(context, frame, cancellationToken);
                    if (message.Kind is StreamMessageKind.Gone or StreamMessageKind.Closed) return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnects are normal; the finally block releases its bounded channel.
        }
        catch (TimeoutException)
        {
            context.Abort();
        }
        finally { hub.Unsubscribe(subscription); }
    }

    private static async Task WriteAndFlushAsync(HttpContext context, string frame, CancellationToken requestCancellation)
    {
        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, context.RequestAborted);
        writeTimeout.CancelAfter(WriteTimeout);
        try { await context.Response.WriteAsync(frame, writeTimeout.Token); }
        catch (OperationCanceledException) when (!requestCancellation.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested) { throw new TimeoutException(); }

        using var flushTimeout = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, context.RequestAborted);
        flushTimeout.CancelAfter(WriteTimeout);
        try { await context.Response.Body.FlushAsync(flushTimeout.Token); }
        catch (OperationCanceledException) when (!requestCancellation.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested) { throw new TimeoutException(); }
    }

    private static string Frame(StreamMessage message) => message.Kind switch
    {
        StreamMessageKind.View => $"event: view\ndata: {message.Json}\n\n",
        StreamMessageKind.Config => $"event: config\ndata: {JsonSerializer.Serialize(new { version = message.Version })}\n\n",
        StreamMessageKind.Inbox => $"event: inbox\ndata: {message.Json ?? JsonSerializer.Serialize(new { version = message.Version })}\n\n",
        StreamMessageKind.Gone => $"event: gone\ndata: {JsonSerializer.Serialize(new { reason = message.Reason })}\n\n",
        StreamMessageKind.Closed => $"event: closed\ndata: {JsonSerializer.Serialize(new { reason = message.Reason })}\n\n",
        _ => throw new ArgumentOutOfRangeException(nameof(message))
    };

    private static PlayerIdentity? Player(HttpContext context)
    {
#if QUESSHI_TEST_IDENTITY
        return context.Request.Headers.TryGetValue("X-Quesshi-Test-Player", out var value) && Guid.TryParse(value, out var id)
            ? new PlayerIdentity(TenantResolutionMiddleware.GetTenant(context)?.Id ?? Guid.Empty, id, null, false) : null;
#else
        return context.Player();
#endif
    }
}
