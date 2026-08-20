namespace Quesshi.Application.UseCases;

public enum AdminLoginStatus
{
    Ok,

    /// <summary>Wrong password, unknown username, or a deactivated account — deliberately indistinguishable.</summary>
    InvalidCredentials,

    Locked,

    /// <summary>Correct password, but the account was created with a temporary one.</summary>
    MustChangePassword
}
