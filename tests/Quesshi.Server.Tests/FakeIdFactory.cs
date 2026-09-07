using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

public sealed class FakeIdFactory : IIdFactory
{
    private int _n;
    public string NewId() => $"id-{++_n}";
    public string NewMatchCode() => $"CODE{++_n}";
}
