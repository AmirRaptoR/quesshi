using Orleans;

namespace Quesshi.Grains.Abstractions;

/// <summary>Test-only grain used to verify that membership routes calls between silos.</summary>
public interface IPingGrain : IGrainWithStringKey
{
    Task<string> PingAsync();
}
