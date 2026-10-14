namespace Quesshi.Application.Identity;

internal sealed record EmailAuthOptions(int EmailLimit, int AddressLimit, TimeSpan RateLimitWindow, TimeSpan ProfileTokenLifetime)
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

internal sealed record ProfileSeed(string DisplayName, string AvatarSeed, string Language);

internal enum TenantPlayerState { Missing, Live, Unavailable }

internal sealed record TenantPlayer(TenantPlayerState State, Guid? PlayerId = null);

internal enum ProfileConfirmationState { Created, Existing, Unavailable, Invalid }

internal sealed record ProfileConfirmation(ProfileConfirmationState State, Guid? PlayerId = null);

internal interface IEmailCodeSender
{
    Task SendAsync(string email, string code, string language, CancellationToken cancellationToken = default);
}
