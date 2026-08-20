using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure;

public sealed class IdFactory : IIdFactory
{
    // No I, O, 0 or 1 — these get read aloud and typed in by hand.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string NewId() => Guid.NewGuid().ToString("N");

    public string NewMatchCode()
        => string.Create(6, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = CodeAlphabet[Random.Shared.Next(CodeAlphabet.Length)];
        });
}
