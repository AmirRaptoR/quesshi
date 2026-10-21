using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Quesshi.Server.Tests;

/// <summary>Hands out OS-assigned loopback ports so concurrent test runs never share a fixed port.</summary>
internal static class TestPorts
{
    private static readonly ConcurrentDictionary<int, byte> Reserved = new();

    /// <summary>Returns a port the OS assigned that no other caller in this process holds.</summary>
    public static int Allocate()
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (Reserved.TryAdd(port, 0)) return port;
        }
    }

    /// <summary>Makes a port available to <see cref="Allocate"/> again.</summary>
    public static void Release(int port) => Reserved.TryRemove(port, out _);
}
