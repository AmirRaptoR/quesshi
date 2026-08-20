using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
    public static FakeClock At2026() => new(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
}
