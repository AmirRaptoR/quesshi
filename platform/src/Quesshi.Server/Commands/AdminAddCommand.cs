using Quesshi.Infrastructure.Identity;

namespace Quesshi.Server.Commands;

internal sealed class AdminAddCommand(AdminAccountCreator accounts)
{
    public async Task<Guid> ExecuteAsync(AdminAddOptions options, CancellationToken cancellationToken = default)
    {
        var result = await accounts.CreateAsync(new(
            options.Username,
            options.Email,
            options.Password,
            options.IsPlatform,
            MustChangePassword: false,
            TenantSlug: options.TenantSlug), cancellationToken);

        return result.Status switch
        {
            AdminAccountCreationStatus.Created => result.AdminId!.Value,
            AdminAccountCreationStatus.InvalidUsername => throw new CommandException("<username> is required."),
            AdminAccountCreationStatus.InvalidEmail => throw new CommandException("<email> must be a valid email address."),
            AdminAccountCreationStatus.WeakPassword => throw new CommandException("The supplied password does not meet the password policy."),
            AdminAccountCreationStatus.TenantNotFound => throw new CommandException("The requested tenant does not exist."),
            AdminAccountCreationStatus.TenantAlreadyHasAdmin => throw new InvalidOperationException("Unexpected bootstrap-only account creation status."),
            AdminAccountCreationStatus.DuplicateIdentity => throw new CommandException("An administrator with that username or email already exists."),
            _ => throw new InvalidOperationException("Unexpected admin account creation status.")
        };
    }
}
