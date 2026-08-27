using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public interface IOtpSender
{
    /// <summary>Null from every sender that ships: the code reaches the player by mail or by log, never
    /// as the answer to the request that asked for it.</summary>
    Task<string?> SendAsync(string email, string code, Language lang, CancellationToken ct = default);
}
