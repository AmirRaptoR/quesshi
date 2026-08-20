using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public interface IOtpStore
{
    Task SaveAsync(OtpChallenge challenge, CancellationToken ct = default);
    Task<OtpChallenge?> GetAsync(string email, CancellationToken ct = default);
    Task DeleteAsync(string email, CancellationToken ct = default);
}
