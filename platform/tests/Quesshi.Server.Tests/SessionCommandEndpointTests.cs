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
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Sessions;
using Quesshi.Server.Tenants;
using Quesshi.Shared;

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
    public async Task Roster_route_pages_for_a_seated_viewer_and_hides_unseated_requests()
    {
        var tenant = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        var session = Guid.NewGuid();
        var viewer = Guid.NewGuid();
        var snapshots = new RosterSnapshots(new RosterPage(
            [new SessionRosterSeat(viewer.ToString(), "Viewer", 10, "player", "active", 0, null, null)], 10));
        var context = Context("");
        context.Items[typeof(Tenant)] = tenant;
        context.Request.Headers["X-Quesshi-Test-Player"] = viewer.ToString();

        var page = await RosterAsync(context, session, snapshots, 9, 25);
        await page.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal((tenant.Id, session, viewer.ToString(), 9, 25), snapshots.Request);
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
        Assert.Equal(10, document.RootElement.GetProperty("nextSeat").GetInt32());

        snapshots.Page = null;
        context.Response.Body.SetLength(0);
        var hidden = await RosterAsync(context, session, snapshots, null, 50);
        await hidden.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

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
    public async Task Mapped_route_maps_grain_errors_uses_the_resolved_tenant_and_accepts_an_exact_bounded_body()
    {
        var port = AvailablePort();
        var firstTenant = new Tenant(Guid.NewGuid(), "first", "First", "en");
        var secondTenant = new Tenant(Guid.NewGuid(), "second", "Second", "en");
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var grain = DispatchProxy.Create<ISessionGrain, SessionGrainProxy>();
        var grainProxy = (SessionGrainProxy)(object)grain;
        var factory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var factoryProxy = (GrainFactoryProxy)(object)factory;
        factoryProxy.Grain = grain;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton<ITenants>(new MultipleTenants(firstTenant, secondTenant));
        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton<IClock>(clock);
        await using var app = builder.Build();
        app.UseTenantResolution();
        app.MapSessionCommands();
        await app.StartAsync();
        try
        {
            var player = Guid.NewGuid();
            var session = Guid.NewGuid();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            grainProxy.Result = SessionCommandResult.Error("not_found");
            using var missing = CommandRequest(CommandBody, player, session, "first.example.test");
            await AssertErrorAsync(await client.SendAsync(missing), HttpStatusCode.NotFound, "not_found");
            Assert.Equal($"{firstTenant.Id}/{session}", factoryProxy.Key);

            grainProxy.Result = SessionCommandResult.Error("absent_seat");
            using var absent = CommandRequest(CommandBody, player, session, "first.example.test");
            await AssertErrorAsync(await client.SendAsync(absent), HttpStatusCode.Forbidden, "absent_seat");

            grainProxy.Result = SessionCommandResult.Error("invalid_command");
            using var invalid = CommandRequest(CommandBody, player, session, "second.example.test");
            await AssertErrorAsync(await client.SendAsync(invalid), HttpStatusCode.BadRequest, "invalid_command");
            Assert.Equal($"{secondTenant.Id}/{session}", factoryProxy.Key);

            grainProxy.Result = SessionCommandResult.Error("session_busy", retryable: true);
            using var busy = CommandRequest(CommandBody, player, session, "first.example.test");
            await AssertErrorAsync(await client.SendAsync(busy), HttpStatusCode.Conflict, "session_busy");

            grainProxy.Result = SessionCommandResult.Error("stale_activation", retryable: true);
            using var stale = CommandRequest(CommandBody, player, session, "first.example.test");
            await AssertErrorAsync(await client.SendAsync(stale), HttpStatusCode.Conflict, "stale_activation");

            grainProxy.Result = SessionCommandResult.Error("late_answer");
            using var late = CommandRequest(CommandBody, player, session, "first.example.test");
            await AssertErrorAsync(await client.SendAsync(late), HttpStatusCode.Conflict, "late_answer");

            using var missingFields = CommandRequest("{}", player, session, "first.example.test");
            await AssertErrorAsync(await client.SendAsync(missingFields), HttpStatusCode.BadRequest, "malformed_command");

            grainProxy.Result = new SessionCommandResult("ok", false, 1, "{}");
            var exact = CommandBody + new string(' ', 16 * 1024 - Encoding.UTF8.GetByteCount(CommandBody));
            using var bounded = CommandRequest(exact, player, session, "first.example.test");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(bounded)).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Mapped_route_stamps_receipt_after_a_chunked_body_finishes()
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
            var body = Encoding.UTF8.GetBytes(CommandBody);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{Guid.NewGuid()}/commands")
            {
                Content = new ChunkedContent(body, () => clock.Set(DateTimeOffset.UnixEpoch.AddSeconds(2))),
                Headers = { Host = "tenant.example.test" },
            };
            request.Headers.Add("X-Quesshi-Test-Player", player.ToString());
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
            Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2), grainProxy.Command!.ReceivedAt);
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

    private const string CommandBody = """{"type":"answer","at":{"phase":"0","slot":0,"step":0}}""";

    private static HttpRequestMessage CommandRequest(string body, Guid player, Guid? session = null, string host = "tenant.example.test")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{session ?? Guid.NewGuid()}/commands")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Host = host;
        request.Headers.Add("X-Quesshi-Test-Player", player.ToString());
        return request;
    }

    private static Task<IResult> ExecuteAsync(HttpContext context, Guid sessionId, IGrainFactory grains, IClock clock) =>
        (Task<IResult>)typeof(SessionCommandEndpoints)
            .GetMethod("ExecuteAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, sessionId, grains, clock, CancellationToken.None])!;

    private static Task<IResult> RosterAsync(HttpContext context, Guid sessionId, ISessionSnapshotLoader snapshots, int? after, int? take) =>
        (Task<IResult>)typeof(SessionCommandEndpoints)
            .GetMethod("RosterAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, sessionId, snapshots, after, take, CancellationToken.None])!;

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

    private sealed class MultipleTenants(Tenant first, Tenant second) : ITenants
    {
        public Task<Tenant?> ResolveAsync(string host, CancellationToken cancellationToken = default) => Task.FromResult<Tenant?>(host switch
        {
            "first.example.test" => first,
            "second.example.test" => second,
            _ => null,
        });
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public ISessionGrain Grain { get; set; } = null!;
        public string? Key { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != "GetGrain") throw new NotSupportedException(targetMethod.Name);
            Key = args!.OfType<string>().SingleOrDefault();
            return Grain;
        }
    }

    private class SessionGrainProxy : DispatchProxy
    {
        public SessionCommand? Command { get; private set; }
        public SessionCommandResult Result { get; set; } = new("ok", false, 1, "{}");

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(ISessionGrain.ExecuteAsync)) throw new NotSupportedException(targetMethod.Name);
            Command = (SessionCommand)args![0]!;
            return Task.FromResult(Result);
        }
    }

    private sealed class RosterSnapshots(RosterPage? page) : ISessionSnapshotLoader
    {
        public RosterPage? Page { get; set; } = page;
        public (Guid Tenant, Guid Session, string Viewer, int? After, int Take) Request { get; private set; }
        public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult<SessionState?>(null);
        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId, IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionItemState>>([]);
        public Task<RosterPage?> LoadRosterPageAsync(Guid tenantId, Guid sessionId, string viewer, int? afterSeat, int take, CancellationToken cancellationToken = default)
        {
            Request = (tenantId, sessionId, viewer, afterSeat, take);
            return Task.FromResult(Page);
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

    private sealed class ChunkedContent(byte[] bytes, Action completed) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => WriteAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) => WriteAsync(stream, cancellationToken);

        private async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
        {
            var split = Math.Max(1, bytes.Length / 2);
            await stream.WriteAsync(bytes.AsMemory(0, split), cancellationToken);
            completed();
            await stream.WriteAsync(bytes.AsMemory(split), cancellationToken);
        }
    }
}
