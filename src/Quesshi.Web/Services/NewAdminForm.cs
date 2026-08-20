namespace Quesshi.Web.Services;

/// <summary>The create-an-administrator form on the accounts page.</summary>
public sealed class NewAdminForm
{
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}
