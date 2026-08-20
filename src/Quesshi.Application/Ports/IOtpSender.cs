using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public interface IOtpSender
{
    /// <summary>Returns the code back only when the sender is a development stub, so the UI can show it.</summary>
    Task<string?> SendAsync(string email, string code, Language lang, CancellationToken ct = default);
}
