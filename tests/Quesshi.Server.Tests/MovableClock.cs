using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class MovableClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => Now += by;
}
