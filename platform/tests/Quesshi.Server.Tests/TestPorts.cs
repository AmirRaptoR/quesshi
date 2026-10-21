using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Quesshi.Server.Tests;

/// <summary>
/// Hands out loopback ports that the OS confirms are free and that no concurrent test run holds. Ports come
/// from below the OS ephemeral range, so another process's outgoing connection can never be given the port
/// between allocation and the host binding it.
/// </summary>
internal static class TestPorts
{
    private const int PoolStart = 20000;
    private const int FallbackPoolEnd = 32768;

    private static readonly ConcurrentDictionary<int, FileStream> Reserved = new();
    private static readonly int PoolEnd = ReadEphemeralStart();

    /// <summary>
    /// Returns a port the OS confirmed bindable and that no other caller, in this process or in a concurrent
    /// test process on the machine, holds. The reservation is an exclusive lock on a per-port file
    /// that lasts until <see cref="Release"/> (or process exit).
    /// </summary>
    public static int Allocate()
    {
        while (true)
        {
            var port = Random.Shared.Next(PoolStart, PoolEnd);
            var reservation = TryReserve(port);
            if (reservation is null) continue;
            if (IsBindable(port) && Reserved.TryAdd(port, reservation)) return port;
            reservation.Dispose();
        }
    }

    private static int ReadEphemeralStart()
    {
        try
        {
            var low = int.Parse(File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]);
            return low > PoolStart + 1000 ? low : FallbackPoolEnd;
        }
        catch (Exception)
        {
            return FallbackPoolEnd;
        }
    }

    private static bool IsBindable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Makes a port available to <see cref="Allocate"/> again.</summary>
    public static void Release(int port)
    {
        if (Reserved.TryRemove(port, out var reservation)) reservation.Dispose();
    }

    private static FileStream? TryReserve(int port)
    {
        try
        {
            return new FileStream(
                Path.Combine(Path.GetTempPath(), $"quesshi-test-port-{port}.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
