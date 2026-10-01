using Orleans.Runtime;

namespace Quesshi.Infrastructure;

/// <summary>Request-local tenant identity propagated to Orleans calls and available to repositories.</summary>
public sealed class TenantContext
{
    public const string RequestContextKey = "quesshi.tenant";
    private static readonly AsyncLocal<string?> Ambient = new();

    public string Id => RequestContext.Get(RequestContextKey) as string ?? Ambient.Value ?? "quesshi";

    public string Key(string logicalKey)
    {
        var suffix = logicalKey.StartsWith("quesshi:", StringComparison.Ordinal)
            ? logicalKey["quesshi:".Length..]
            : logicalKey;
        return Id == "quesshi" ? logicalKey : $"quesshi:{Id}:{suffix}";
    }

    public IDisposable Enter(string tenantId)
    {
        var previousAmbient = Ambient.Value;
        var previousRequest = RequestContext.Get(RequestContextKey);
        Ambient.Value = tenantId;
        RequestContext.Set(RequestContextKey, tenantId);
        return new Restore(() =>
        {
            Ambient.Value = previousAmbient;
            if (previousRequest is null) RequestContext.Remove(RequestContextKey);
            else RequestContext.Set(RequestContextKey, previousRequest);
        });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
