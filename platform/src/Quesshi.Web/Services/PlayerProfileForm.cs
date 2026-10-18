using Quesshi.Shared;

namespace Quesshi.Web.Services;

public sealed class PlayerProfileForm
{
    public string DisplayName { get; set; } = string.Empty;
    public string AvatarSeed { get; set; } = PlayerAvatarPalette.Default;
    public string Lang { get; set; } = string.Empty;

    public bool IsValid => DisplayName.Trim().Length is >= 2 and <= 24
        && PlayerAvatarPalette.IsValid(AvatarSeed)
        && !string.IsNullOrWhiteSpace(Lang);
}
