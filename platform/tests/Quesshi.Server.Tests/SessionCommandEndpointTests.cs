using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Quesshi.Application.Tenancy;
using Quesshi.Engine.Contracts;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Sessions;

namespace Quesshi.Server.Tests;

public sealed class SessionCommandEndpointTests
{
    [Fact]
    public async Task Oversized_bodies_return_a_stable_413_error()
    {
        var context = Context(new string('x', 16 * 1024 + 1));
        context.Request.ContentLength = 16 * 1024 + 1;

        var result = await ExecuteAsync(context, Guid.NewGuid(), null!, new TestClock(DateTimeOffset.UnixEpoch));

        await result.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Contains("body_too_large", await new StreamReader(context.Response.Body).ReadToEndAsync(), StringComparison.Ordinal);
    }

#if QUESSHI_TEST_IDENTITY
    [Fact]
    public async Task Test_identity_is_compile_time_gated_and_receipt_is_taken_after_the_body()
    {
        var playerId = Guid.NewGuid();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        const string body = """{"type":"answer","at":{"phase":"0","slot":0,"step":0}}""";
        var context = Context(body);
        context.Request.Headers["X-Quesshi-Test-Player"] = playerId.ToString();
        context.Items[typeof(Tenant)] = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        context.Request.Body = new ReceiptStream(Encoding.UTF8.GetBytes(body), () => clock.Set(DateTimeOffset.UnixEpoch.AddSeconds(2)));
        var grain = DispatchProxy.Create<ISessionGrain, SessionGrainProxy>();
        var grainProxy = (SessionGrainProxy)(object)grain;
        var factory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)factory).Grain = grain;

        var result = await ExecuteAsync(context, Guid.NewGuid(), factory, clock);

        await result.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(playerId.ToString(), grainProxy.Command!.PlayerId);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2), grainProxy.Command.ReceivedAt);
    }
#else
    [Fact]
    public async Task Test_identity_header_is_rejected_without_the_compile_time_test_flag()
    {
        var context = Context("""{"type":"answer","at":{"phase":"0","slot":0,"step":0}}""");
        context.Request.Headers["X-Quesshi-Test-Player"] = Guid.NewGuid().ToString();
        context.Items[typeof(Tenant)] = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");

        var result = await ExecuteAsync(context, Guid.NewGuid(), null!, new TestClock(DateTimeOffset.UnixEpoch));

        await result.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Contains("identity_required", await new StreamReader(context.Response.Body).ReadToEndAsync(), StringComparison.Ordinal);
    }
#endif

    private static DefaultHttpContext Context(string body)
    {
        var context = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddLogging();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static Task<IResult> ExecuteAsync(HttpContext context, Guid sessionId, IGrainFactory grains, IClock clock) =>
        (Task<IResult>)typeof(SessionCommandEndpoints)
            .GetMethod("ExecuteAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, sessionId, grains, clock, CancellationToken.None])!;

    private class GrainFactoryProxy : DispatchProxy
    {
        public ISessionGrain Grain { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == "GetGrain" ? Grain : throw new NotSupportedException(targetMethod.Name);
    }

    private class SessionGrainProxy : DispatchProxy
    {
        public SessionCommand? Command { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(ISessionGrain.ExecuteAsync)) throw new NotSupportedException(targetMethod.Name);
            Command = (SessionCommand)args![0]!;
            return Task.FromResult(new SessionCommandResult("ok", false, 1, "{}"));
        }
    }

    private sealed class ReceiptStream(byte[] content, Action completed) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read == 0) completed();
            return read;
        }
    }
}
