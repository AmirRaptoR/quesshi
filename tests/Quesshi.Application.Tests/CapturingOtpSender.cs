using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class CapturingOtpSender(bool dev = true) : IOtpSender
{
    public string? LastCode { get; private set; }
    public string? LastEmail { get; private set; }
    public Task<string?> SendAsync(string email, string code, Language lang, CancellationToken ct = default)
    {
        LastEmail = email; LastCode = code;
        return Task.FromResult(dev ? code : null);
    }
}
