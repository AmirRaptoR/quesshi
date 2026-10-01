using Quesshi.Server.Tenants;
using Microsoft.AspNetCore.Http;
using Quesshi.Infrastructure;

namespace Quesshi.Server.Tests;

public sealed class TenantRegistryTests
{
    [Fact]
    public void Configured_hosts_resolve_stable_ids_and_public_brand_settings()
    {
        var registry = new TenantRegistry(new TenantOptions
        {
            Tenants =
            [
                new TenantDefinition
                {
                    Id = "brand-a", Name = "Brand A", Hosts = ["a.test"], Theme = "violet",
                    LandingContent = new() { ["en"] = "Welcome" },
                    Languages = ["fa", "en", "nl"], EnabledModes = ["live"],
                    Rules = new() { ["rounds"] = "10" }
                },
                new TenantDefinition { Id = "brand-b", Name = "Brand B", Hosts = ["b.test"], Theme = "blue" }
            ]
        });

        Assert.True(registry.TryResolve("A.TEST:443", out var a));
        Assert.Equal("brand-a", a.Id);
        Assert.Equal("violet", a.Brand.Theme);
        Assert.Equal(["fa", "en", "nl"], a.Brand.Languages);
        Assert.Equal("Welcome", a.Brand.LandingContent["en"]);
        Assert.Equal("10", a.Brand.Rules["rounds"]);
        Assert.True(registry.TryResolve("b.test", out var b));
        Assert.Equal("brand-b", b.Id);
        Assert.False(registry.TryResolve("unknown.test", out _));
    }

    [Fact]
    public void Duplicate_host_mapping_is_rejected_instead_of_selecting_an_arbitrary_tenant()
    {
        var options = new TenantOptions
        {
            Tenants =
            [
                new TenantDefinition { Id = "one", Name = "One", Hosts = ["same.test"] },
                new TenantDefinition { Id = "two", Name = "Two", Hosts = ["SAME.TEST"] }
            ]
        };

        Assert.Throws<InvalidOperationException>(() => new TenantRegistry(options));
    }

    [Fact]
    public async Task Forwarded_host_does_not_select_a_tenant_and_unknown_hosts_fail_closed()
    {
        var registry = new TenantRegistry(new TenantOptions
        {
            Tenants =
            [
                new TenantDefinition { Id = "quesshi", Name = "Quesshi", Hosts = ["quesshi.localhost"] },
                new TenantDefinition { Id = "quessher", Name = "Quessher", Hosts = ["quessher.localhost"] }
            ]
        });
        var tenant = new TenantContext();
        var reached = false;
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            reached = true;
            Assert.Equal("quesshi", tenant.Id);
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("quesshi.localhost");
        context.Request.Headers["X-Forwarded-Host"] = "quessher.localhost";

        await middleware.InvokeAsync(context, registry, tenant);
        Assert.True(reached);

        reached = false;
        context = new DefaultHttpContext();
        context.Request.Host = new HostString("unknown.localhost");
        context.Request.Headers["X-Forwarded-Host"] = "quessher.localhost";
        await middleware.InvokeAsync(context, registry, tenant);
        Assert.False(reached);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }
}
