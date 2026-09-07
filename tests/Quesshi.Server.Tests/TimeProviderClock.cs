using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

/// <summary>Backs <see cref="IClock"/> with the same <see cref="TimeProvider"/> Orleans reads for grain
/// timers, so advancing one advances the other.</summary>
public sealed class TimeProviderClock(TimeProvider timeProvider) : IClock
{
    public DateTimeOffset Now => timeProvider.GetUtcNow();
}
