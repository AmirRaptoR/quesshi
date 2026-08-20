using Quesshi.Application.Ports;

namespace Quesshi.Application.Tests;

public sealed class CapturingAdminMailer : IAdminMailer
{
    public string? LastEmail { get; private set; }
    public string? LastLink { get; private set; }
    public int Sends { get; private set; }

    public Task<string?> SendPasswordResetAsync(string email, string username, string resetLink, CancellationToken ct = default)
    {
        LastEmail = email;
        LastLink = resetLink;
        Sends++;
        return Task.FromResult<string?>(resetLink);
    }
}
