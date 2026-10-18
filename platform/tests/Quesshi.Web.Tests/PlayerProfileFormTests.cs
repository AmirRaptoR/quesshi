using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class PlayerProfileFormTests
{
    [Fact]
    public void A_name_shorter_than_two_characters_is_invalid()
    {
        var form = Form("A", "avatar-1");

        Assert.False(form.IsValid);
    }

    [Fact]
    public void A_name_longer_than_twenty_four_characters_is_invalid()
    {
        var form = Form(new string('A', 25), "avatar-1");

        Assert.False(form.IsValid);
    }

    [Fact]
    public void Whitespace_around_a_valid_name_is_accepted()
    {
        var form = Form("  Ada  ", "avatar-1");

        Assert.True(form.IsValid);
    }

    [Fact]
    public void An_avatar_outside_the_palette_is_invalid()
    {
        var form = Form("Ada", "not-a-palette-avatar");

        Assert.False(form.IsValid);
    }

    private static PlayerProfileForm Form(string displayName, string avatarSeed) => new()
    {
        DisplayName = displayName,
        AvatarSeed = avatarSeed,
        Lang = "en",
    };
}
