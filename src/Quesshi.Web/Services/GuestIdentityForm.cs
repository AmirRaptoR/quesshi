using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// The lobby page's guest identity editor (issue #53, spec section 4): name and avatar seed are the
/// whole of a guest's editable identity — see <c>UpdateProfileDto</c>'s own remarks for why nothing
/// else is worth changing here, and none is invented. A plain mutable POCO bound by <c>@bind</c>, the
/// same shape every other <c>*Form</c> class in this project already takes (see
/// <see cref="CategoryForm"/>), so the validation it gates Save on is unit-testable with no bUnit.
/// </summary>
public sealed class GuestIdentityForm
{
    public string DisplayName { get; set; } = "";
    public string AvatarSeed { get; set; } = "";

    public static GuestIdentityForm From(MeDto me) => new() { DisplayName = me.DisplayName, AvatarSeed = me.AvatarSeed };

    /// <summary>
    /// Mirrors <c>PUT /api/me</c>'s own name-length guard (<c>GameEndpoints.cs</c>: 2 to 24
    /// characters) so the Save button disables before a doomed request ever goes out, plus the
    /// avatar-seed check <see cref="AvatarPalette"/>'s own remarks explain. Trimmed the same way the
    /// server trims before measuring.
    /// </summary>
    public bool IsValid => DisplayName.Trim().Length is >= 2 and <= 24 && AvatarPalette.IsValid(AvatarSeed);
}
