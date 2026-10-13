using Orleans;
using Orleans.Placement;
using Orleans.Runtime;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Grains;

[RandomPlacement]
public sealed class PingGrain(IGrainContext context) : Grain, IPingGrain
{
    public Task<string> PingAsync() => Task.FromResult(context.Address.SiloAddress?.ToParsableString() ?? throw new InvalidOperationException("Ping grain has no silo address."));
}
