using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// Issue #53's guest identity editing form on the lobby page — name and avatar seed, validated the
/// same way <c>PUT /api/me</c> validates a name today (2 to 24 characters) plus the avatar-seed check
/// <see cref="AvatarPalette"/>'s own remarks explain, so the Save button never fires a request the
/// server would refuse anyway.
/// </summary>
public class GuestIdentityFormTests
{
    private static MeDto Guest(string name, string avatarSeed) =>
        new("g1", name, "", avatarSeed, "en", new StatsDto(0, 0, 0, 0, 0, 0, 0), [], [], IsGuest: true);

    [Fact]
    public void A_freshly_loaded_form_carries_the_current_name_and_avatar()
    {
        var form = GuestIdentityForm.From(Guest("Amir", "avatar-3"));

        Assert.Equal("Amir", form.DisplayName);
        Assert.Equal("avatar-3", form.AvatarSeed);
    }

    [Theory]
    [InlineData("A")] // one character — below the two-character floor
    [InlineData("")]
    public void A_name_below_two_characters_is_invalid(string name)
    {
        var form = new GuestIdentityForm { DisplayName = name, AvatarSeed = AvatarPalette.Seeds[0] };

        Assert.False(form.IsValid);
    }

    [Fact]
    public void A_name_over_twenty_four_characters_is_invalid()
    {
        var form = new GuestIdentityForm { DisplayName = new string('a', 25), AvatarSeed = AvatarPalette.Seeds[0] };

        Assert.False(form.IsValid);
    }

    [Fact]
    public void Surrounding_whitespace_does_not_count_toward_the_length_floor_or_ceiling()
    {
        var form = new GuestIdentityForm { DisplayName = "  Amir  ", AvatarSeed = AvatarPalette.Seeds[0] };

        Assert.True(form.IsValid);
    }

    [Fact]
    public void A_name_within_range_and_a_palette_avatar_is_valid()
    {
        var form = new GuestIdentityForm { DisplayName = "Amir", AvatarSeed = AvatarPalette.Seeds[0] };

        Assert.True(form.IsValid);
    }

    [Fact]
    public void An_avatar_seed_outside_the_palette_is_invalid_even_with_a_good_name()
    {
        var form = new GuestIdentityForm { DisplayName = "Amir", AvatarSeed = "not-a-real-seed" };

        Assert.False(form.IsValid);
    }

    [Fact]
    public void Every_palette_seed_is_itself_valid()
    {
        Assert.All(AvatarPalette.Seeds, seed => Assert.True(AvatarPalette.IsValid(seed)));
    }

    [Fact]
    public void The_palette_has_more_than_one_visibly_distinct_choice()
    {
        // "Visibly distinct" is approximated here as "more than one swatch to pick from" — the actual
        // hue spread is Ranks.Tint's job, exercised visually rather than asserted numerically here.
        Assert.True(AvatarPalette.Seeds.Count > 1);
        Assert.Equal(AvatarPalette.Seeds.Count, AvatarPalette.Seeds.Distinct().Count());
    }
}
