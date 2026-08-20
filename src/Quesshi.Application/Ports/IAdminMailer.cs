namespace Quesshi.Application.Ports;

public interface IAdminMailer
{
    /// <summary>Returns the link back only when the sender is a development stub, so it can be shown locally.</summary>
    Task<string?> SendPasswordResetAsync(string email, string username, string resetLink, CancellationToken ct = default);
}
