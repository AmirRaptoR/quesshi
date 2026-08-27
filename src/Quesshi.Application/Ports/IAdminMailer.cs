namespace Quesshi.Application.Ports;

public interface IAdminMailer
{
    /// <summary>Null from every sender that ships, for the reason given on <see cref="IOtpSender"/>.</summary>
    Task<string?> SendPasswordResetAsync(string email, string username, string resetLink, CancellationToken ct = default);
}
