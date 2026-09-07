using System.Reflection;
using Orleans;

namespace Quesshi.Server.Tests;

/// <summary>
/// Wraps a real <see cref="IGrainFactory"/> and records every <c>GetGrain&lt;T&gt;(string)</c> call
/// made through it, so a test can assert a grain of a given type was — or, as the match list promises
/// for a live row, was never — asked for by a given id. Orleans exposes no activation counter of its
/// own; this is the cheapest thing that can stand in for one without touching production code, since
/// <see cref="Server.Api.GameEndpoints.ListMatchesAsync"/> already takes <see cref="IGrainFactory"/>
/// as a plain parameter.
/// </summary>
public class GrainActivationSpy : DispatchProxy
{
    private IGrainFactory _inner = null!;
    private List<(Type GrainInterface, string Key)> _requests = null!;

    public static IGrainFactory Wrap(IGrainFactory inner, out List<(Type GrainInterface, string Key)> requests)
    {
        var proxy = Create<IGrainFactory, GrainActivationSpy>();
        var spy = (GrainActivationSpy)(object)proxy;
        spy._inner = inner;
        requests = spy._requests = [];
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IGrainFactory.GetGrain) && targetMethod.IsGenericMethod
            && args is [string key, ..])
            _requests.Add((targetMethod.GetGenericArguments()[0], key));

        return targetMethod.Invoke(_inner, args);
    }
}
