using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class SeqIds : IIdFactory
{
    private int _n;
    public string NewId() => $"id-{++_n}";
    public string NewMatchCode() => $"CODE{++_n}";
}
