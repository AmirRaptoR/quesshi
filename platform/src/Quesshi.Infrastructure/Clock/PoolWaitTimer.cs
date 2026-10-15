using System.Diagnostics;

namespace Quesshi.Infrastructure.Clock;

internal static class PoolWaitTimer
{
    public static long Start() => Stopwatch.GetTimestamp();

    public static TimeSpan Elapsed(long started) => Stopwatch.GetElapsedTime(started);
}
