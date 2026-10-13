using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Application.Tenancy;
using Quesshi.Infrastructure.Tenancy;

namespace Quesshi.Server.Tenants;

/// <summary>Resolves each request explicitly and never supplies a default tenant.</summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HashSet<IPAddress> _trustedProxies;

    public TenantResolutionMiddleware(RequestDelegate next, IEnumerable<IPAddress> trustedProxies)
    {
        _next = next;
        _trustedProxies = [.. trustedProxies.Select(NormalizeAddress)];
    }

    public async Task InvokeAsync(HttpContext context, ITenants tenants)
    {
        var host = TenantHost.Normalize(SelectedHost(context));
        var tenant = host.Length == 0 ? null : await tenants.ResolveAsync(host, context.RequestAborted);
        if (tenant is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Items[typeof(Tenant)] = tenant;
        await _next(context);
    }

    public static Tenant? GetTenant(HttpContext context) => context.Items.TryGetValue(typeof(Tenant), out var value) ? value as Tenant : null;

    private string SelectedHost(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is not null
            && _trustedProxies.Contains(NormalizeAddress(remoteAddress))
            && context.Request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHost))
        {
            return forwardedHost.ToString().Split(',')[^1].Trim();
        }

        return context.Request.Host.Value ?? "";
    }

    internal static IEnumerable<IPAddress> TrustedProxiesFromEnvironment() =>
        (Environment.GetEnvironmentVariable("TRUSTED_PROXIES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
            .Where(address => address is not null)
            .Cast<IPAddress>();

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>Registration boundary for the future serve-host composition.</summary>
public static class TenantResolutionApplicationBuilderExtensions
{
    public static IServiceCollection AddTenantResolution(this IServiceCollection services)
    {
        services.AddSingleton<ITenants, PostgresTenants>();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var proxy in TenantResolutionMiddleware.TrustedProxiesFromEnvironment()) options.KnownProxies.Add(proxy);
        });
        return services;
    }

    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantResolutionMiddleware>(TenantResolutionMiddleware.TrustedProxiesFromEnvironment());
}
