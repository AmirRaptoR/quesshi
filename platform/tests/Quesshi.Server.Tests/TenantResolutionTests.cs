using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Application.Tenancy;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Tests;

public sealed class TenantResolutionTests
{
    private static readonly Tenant TenantA = new(Guid.Parse("b4e9feeb-51ae-4bb8-aeeb-1d50c81c0cfe"), "tenant-a", "Tenant A", "en");
    private static readonly Tenant TenantB = new(Guid.Parse("b4f0d25c-a0da-4c62-a279-1275fe646b26"), "tenant-b", "Tenant B", "en");

    [Theory]
    [InlineData("EXAMPLE.TEST:443", "example.test")]
    [InlineData("example.test...", "example.test")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("example.test/path", "")]
    [InlineData("example.test:invalid", "")]
    public void Canonical_host_normalization_is_safe_and_matches_storage(string host, string expected)
    {
        Assert.Equal(expected, TenantHost.Normalize(host));
    }

    [Fact]
    public async Task Known_host_is_exposed_explicitly_to_downstream_code()
    {
        var tenants = new StubTenants([("a.example.test", TenantA)]);
        var reached = false;
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            reached = true;
            Assert.Equal(TenantA, TenantResolutionMiddleware.GetTenant(_));
            return Task.CompletedTask;
        }, []);
        var context = Request("A.EXAMPLE.TEST:443");

        await middleware.InvokeAsync(context, tenants);

        Assert.True(reached);
        Assert.Equal(TenantA, TenantResolutionMiddleware.GetTenant(context));
    }

    [Fact]
    public async Task Missing_host_returns_an_empty_404_without_a_default_tenant()
    {
        var middleware = new TenantResolutionMiddleware(_ => throw new Xunit.Sdk.XunitException("The next handler must not run."), []);
        var context = Request("unknown.example.test");

        await middleware.InvokeAsync(context, new StubTenants([]));

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Null(TenantResolutionMiddleware.GetTenant(context));
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task Forwarded_host_is_used_only_for_a_trusted_immediate_peer()
    {
        var tenants = new StubTenants([("a.example.test", TenantA), ("b.example.test", TenantB)]);
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask, [IPAddress.Parse("10.0.0.2")]);
        var trusted = Request("a.example.test", "client.example.test, b.example.test", "::ffff:10.0.0.2");
        var untrusted = Request("a.example.test", "b.example.test", "10.0.0.3");

        await middleware.InvokeAsync(trusted, tenants);
        await middleware.InvokeAsync(untrusted, tenants);

        Assert.Equal(TenantB, TenantResolutionMiddleware.GetTenant(trusted));
        Assert.Equal(TenantA, TenantResolutionMiddleware.GetTenant(untrusted));
    }

    [Fact]
    public async Task Registration_loads_trusted_proxies_for_the_real_middleware_pipeline()
    {
        const string trustedProxy = "10.0.0.2";
        var originalTrustedProxies = Environment.GetEnvironmentVariable("TRUSTED_PROXIES");
        try
        {
            Environment.SetEnvironmentVariable("TRUSTED_PROXIES", trustedProxy);
            using var services = new ServiceCollection()
                .AddSingleton<ITenants>(new StubTenants([("a.example.test", TenantA), ("b.example.test", TenantB)]))
                .BuildServiceProvider();
            var builder = new ApplicationBuilder(services);
            builder.UseTenantResolution();
            builder.Run(_ => Task.CompletedTask);
            var application = builder.Build();
            var trusted = Request("a.example.test", "b.example.test", trustedProxy);
            var untrusted = Request("a.example.test", "b.example.test", "10.0.0.3");

            await application(trusted);
            await application(untrusted);

            var noPeer = Request("a.example.test", "b.example.test");
            await application(noPeer);

            Assert.Equal(TenantB, TenantResolutionMiddleware.GetTenant(trusted));
            Assert.Equal(TenantA, TenantResolutionMiddleware.GetTenant(untrusted));
            Assert.Equal(TenantA, TenantResolutionMiddleware.GetTenant(noPeer));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRUSTED_PROXIES", originalTrustedProxies);
        }
    }

    [Fact]
    public async Task Registration_honors_forwarded_scheme_only_from_trusted_proxies()
    {
        const string trustedProxy = "10.0.0.2";
        var originalTrustedProxies = Environment.GetEnvironmentVariable("TRUSTED_PROXIES");
        try
        {
            Environment.SetEnvironmentVariable("TRUSTED_PROXIES", trustedProxy);
            using var services = new ServiceCollection().AddLogging().AddTenantResolution().BuildServiceProvider();
            var builder = new ApplicationBuilder(services);
            builder.UseForwardedHeaders();
            builder.Run(_ => Task.CompletedTask);
            var application = builder.Build();
            var trusted = Request("internal.example.test", remoteIp: trustedProxy);
            trusted.Request.Headers["X-Forwarded-Proto"] = "https";
            var untrusted = Request("internal.example.test", remoteIp: "10.0.0.3");
            untrusted.Request.Headers["X-Forwarded-Proto"] = "https";

            await application(trusted);
            await application(untrusted);

            Assert.Equal("https", trusted.Request.Scheme);
            Assert.Equal("http", untrusted.Request.Scheme);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRUSTED_PROXIES", originalTrustedProxies);
        }
    }

    private static DefaultHttpContext Request(string host, string? forwardedHost = null, string? remoteIp = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = HostString.FromUriComponent(host);
        if (forwardedHost is not null) context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        if (remoteIp is not null) context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return context;
    }

    private sealed class StubTenants((string Host, Tenant Tenant)[] entries) : ITenants
    {
        private readonly IReadOnlyDictionary<string, Tenant> _entries = entries.ToDictionary(entry => entry.Host, entry => entry.Tenant, StringComparer.Ordinal);

        public Task<Tenant?> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.TryGetValue(host, out var tenant) ? tenant : null);
    }
}
