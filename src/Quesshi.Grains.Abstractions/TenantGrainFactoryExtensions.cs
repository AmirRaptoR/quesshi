using Orleans;
using Orleans.Runtime;

namespace Quesshi.Grains.Abstractions;

/// <summary>Addresses a grain within the tenant carried by the current Orleans request.</summary>
public static class TenantGrainFactoryExtensions
{
    private const string TenantKey = "quesshi.tenant";

    public static T GetTenantGrain<T>(this IGrainFactory factory, string id) where T : IGrainWithStringKey
        => factory.GetGrain<T>(TenantGrainAddress.StringKey(id, TenantId()));

    public static T GetTenantGrain<T>(this IGrainFactory factory, long id) where T : IGrainWithIntegerCompoundKey
    {
        var tenantId = TenantId();
        var address = tenantId is null or "quesshi"
            ? factory.GetGrain(typeof(T), id)
            : factory.GetGrain(typeof(T), id, tenantId);
        return (T)address;
    }

    private static string? TenantId() => RequestContext.Get(TenantKey) as string;
}

public static class TenantGrainAddress
{
    public static string TenantId(GrainId grainId)
    {
        if (grainId.TryGetIntegerKey(out _, out var keyExtension))
            return string.IsNullOrEmpty(keyExtension) ? "quesshi" : keyExtension;

        return TenantId(grainId.Key.ToString());
    }

    public static string TenantId(string grainKey)
    {
        var separator = grainKey.IndexOf(':');
        if (separator > 0 && grainKey[..separator].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
            return grainKey[..separator];
        return "quesshi";
    }

    public static IDisposable Enter(string grainKey)
    {
        var priorTenant = RequestContext.Get("quesshi.tenant");
        RequestContext.Set("quesshi.tenant", TenantId(grainKey));
        return new RestoreContext(priorTenant);
    }

    public static string StringKey(string id, string? tenantId)
        => tenantId is null or "quesshi" ? id : $"{tenantId}:{id}";

    public static string? IntegerKeyExtension(string? tenantId)
        => tenantId is null or "quesshi" ? null : tenantId;

    public static string LogicalStringKey(string grainKey)
    {
        var tenantId = RequestContext.Get("quesshi.tenant") as string;
        var expectedPrefix = tenantId is null or "quesshi" ? null : $"{tenantId}:";
        if (expectedPrefix is not null && grainKey.StartsWith(expectedPrefix, StringComparison.Ordinal))
            return grainKey[expectedPrefix.Length..];

        // Reminder callbacks do not carry the caller's request context. The incoming grain filter
        // derives that context from the namespaced grain id before this method accesses persistence.
        return grainKey;
    }

    private sealed class RestoreContext(object? priorTenant) : IDisposable
    {
        public void Dispose()
        {
            if (priorTenant is null) RequestContext.Remove("quesshi.tenant");
            else RequestContext.Set("quesshi.tenant", priorTenant);
        }
    }
}

/// <summary>Restores tenant context from grain identity for reminder and nested grain calls.</summary>
public sealed class TenantGrainCallFilter : IIncomingGrainCallFilter
{
    private const string TenantKey = "quesshi.tenant";

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        var priorTenant = RequestContext.Get(TenantKey);
        RequestContext.Set(TenantKey, TenantGrainAddress.TenantId(context.TargetContext.GrainId));
        try
        {
            await context.Invoke();
        }
        finally
        {
            if (priorTenant is null) RequestContext.Remove(TenantKey);
            else RequestContext.Set(TenantKey, priorTenant);
        }
    }
}
