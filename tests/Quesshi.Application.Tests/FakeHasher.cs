using Quesshi.Application.Ports;

namespace Quesshi.Application.Tests;

/// <summary>Reversible on purpose: these tests are about the flow, not about the KDF.</summary>
public sealed class FakeHasher : IPasswordHasher
{
    public int BurnCount { get; private set; }

    public string Hash(string password) => "hashed:" + password;

    public bool Verify(string hash, string password) => hash == Hash(password);

    public void BurnTime() => BurnCount++;
}
