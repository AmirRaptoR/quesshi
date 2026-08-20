using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>Reset tokens expire on their own, so the store needs a time-to-live and no delete.</summary>
public interface IResetTokenStore
{
    Task SaveAsync(PasswordResetToken token, CancellationToken ct = default);
    Task<PasswordResetToken?> GetAsync(string secretHash, CancellationToken ct = default);
}
