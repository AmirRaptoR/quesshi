namespace Quesshi.Application.Identity;

public sealed record EmailAuthOptions(int EmailLimit, int AddressLimit, TimeSpan RateLimitWindow, TimeSpan ProfileTokenLifetime)
{
    public static EmailAuthOptions Default { get; } = new(5, 20, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

    public void Validate()
    {
        if (EmailLimit < 1 || AddressLimit < 1 || RateLimitWindow <= TimeSpan.Zero || ProfileTokenLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Email authentication rate limits and lifetimes must be positive.");
        }
    }
}

public sealed record ProfileSeed(string DisplayName, string AvatarSeed, string Language);

public enum TenantPlayerState { Missing, Live, Unavailable }

public sealed record TenantPlayer(TenantPlayerState State, Guid? PlayerId = null);

public enum ProfileConfirmationState { Created, Existing, Unavailable, Invalid }

public sealed record ProfileConfirmation(ProfileConfirmationState State, Guid? PlayerId = null);

public interface IEmailCodeSender
{
    Task SendAsync(string email, string code, string language, CancellationToken cancellationToken = default);
}
