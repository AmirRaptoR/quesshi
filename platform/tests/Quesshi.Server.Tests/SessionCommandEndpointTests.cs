using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Quesshi.Application.Tenancy;
using Quesshi.Engine.Contracts;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Sessions;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Tests;

public sealed class SessionCommandEndpointTests
{
    [Fact]
    public async Task Mapped_route_applies_tenant_resolution_and_returns_stable_input_errors()
    {
        var port = AvailablePort();
        var tenant = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        var grains = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton<ITenants>(new SingleTenant(tenant));
        builder.Services.AddSingleton(grains);
        builder.Services.AddSingleton<IClock>(new TestClock(DateTimeOffset.UnixEpoch));
        await using var app = builder.Build();
        app.UseTenantResolution();
        app.MapSessionCommands();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var malformed = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{Guid.NewGuid()}/commands")
            {
                Content = new StringContent("{", Encoding.UTF8, "application/json"),
                Headers = { Host = "tenant.example.test" },
            };
            using var malformedResponse = await client.SendAsync(malformed);
            await AssertErrorAsync(malformedResponse, HttpStatusCode.BadRequest, "malformed_command");

            using var oversized = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{Guid.NewGuid()}/commands")
            {
                Content = new StringContent(new string('x', 16 * 1024 + 1), Encoding.UTF8, "application/json"),
                Headers = { Host = "tenant.example.test" },
            };
            using var oversizedResponse = await client.SendAsync(oversized);
            await AssertErrorAsync(oversizedResponse, HttpStatusCode.RequestEntityTooLarge, "body_too_large");
#if !QUESSHI_TEST_IDENTITY
            using var identity = CommandRequest("""{"type":"answer","at":{"phase":"0","slot":0,"step":0}}""", Guid.NewGuid());
            using var identityResponse = await client.SendAsync(identity);
            await AssertErrorAsync(identityResponse, HttpStatusCode.Unauthorized, "identity_required");
#endif
        }
        finally { await app.StopAsync(); }
    }

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
    public async Task Mapped_route_accepts_test_identity_only_in_the_test_build()
    {
        var port = AvailablePort();
        var tenant = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var grain = DispatchProxy.Create<ISessionGrain, SessionGrainProxy>();
        var grainProxy = (SessionGrainProxy)(object)grain;
        var factory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)factory).Grain = grain;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton<ITenants>(new SingleTenant(tenant));
        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton<IClock>(clock);
        await using var app = builder.Build();
        app.UseTenantResolution();
        app.MapSessionCommands();
        await app.StartAsync();
        try
        {
            var player = Guid.NewGuid();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var request = CommandRequest("""{"type":"answer","at":{"phase":"0","slot":0,"step":0}}""", player);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(player.ToString(), grainProxy.Command!.PlayerId);
            Assert.Equal(DateTimeOffset.UnixEpoch, grainProxy.Command.ReceivedAt);
        }
        finally { await app.StopAsync(); }
    }

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

    private static HttpRequestMessage CommandRequest(string body, Guid player)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{Guid.NewGuid()}/commands")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Host = "tenant.example.test";
        request.Headers.Add("X-Quesshi-Test-Player", player.ToString());
        return request;
    }

    private static Task<IResult> ExecuteAsync(HttpContext context, Guid sessionId, IGrainFactory grains, IClock clock) =>
        (Task<IResult>)typeof(SessionCommandEndpoints)
            .GetMethod("ExecuteAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, sessionId, grains, clock, CancellationToken.None])!;

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("error").GetString());
    }

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class SingleTenant(Tenant tenant) : ITenants
    {
        public Task<Tenant?> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult<Tenant?>(host == "tenant.example.test" ? tenant : null);
    }

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
