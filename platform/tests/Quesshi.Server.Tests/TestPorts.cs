using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Quesshi.Server.Tests;

/// <summary>Hands out OS-assigned loopback ports so concurrent test runs never share a fixed port.</summary>
internal static class TestPorts
{
    private static readonly ConcurrentDictionary<int, FileStream> Reserved = new();

    /// <summary>
    /// Returns a port the OS assigned and that no other caller, in this process or in a concurrent
    /// test process on the machine, holds. The reservation is an exclusive lock on a per-port file
    /// that lasts until <see cref="Release"/> (or process exit).
    /// </summary>
    public static int Allocate()
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var reservation = TryReserve(port);
            if (reservation is null) continue;
            if (Reserved.TryAdd(port, reservation)) return port;
            reservation.Dispose();
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
