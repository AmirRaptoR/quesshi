using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

public sealed class FakeIdFactory(int start = 0) : IIdFactory
{
    private readonly int _start = start;
    private int _n = start;
    private int _codesReturned;

    /// <summary>
    /// How many times <see cref="NewMatchCode"/> hands back the same colliding code before it starts
    /// minting fresh ones — for driving <c>LiveEndpoints.CreateAsync</c>'s bounded retry loop.
    /// </summary>
    public int CodesToRepeat { get; set; }

    public string NewId() => $"id-{++_n}";

    public string NewMatchCode()
        => _codesReturned++ < CodesToRepeat ? PeekNextCode() : $"CODE{++_n}";

    /// <summary>The colliding code this factory will keep returning until <see cref="CodesToRepeat"/> is exhausted.</summary>
    public string PeekNextCode() => $"CODE{_start}";
}
