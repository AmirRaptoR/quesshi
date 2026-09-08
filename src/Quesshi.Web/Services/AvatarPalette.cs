namespace Quesshi.Web.Services;

/// <summary>
/// The fixed set of avatar seeds a guest may pick from on the lobby page (issue #53, section 4 of
/// the design doc). The doc asks for the seed to be "validated against the palette
/// <c>Ranks.Tint</c> derives colours from" — but issue #54, which adds that validation and
/// <c>Player.SetAvatar</c> server-side, has not landed yet (checked against <c>main</c> before
/// writing this), and <c>Ranks.Tint</c> today has no palette at all: it hashes any string to one of
/// 360 hues (via <c>string.GetHashCode()</c>, whose seed .NET randomizes per process — so even one
/// fixed seed's exact hue can drift between reloads today, a pre-existing property of every avatar in
/// this app, not something this list changes). There is nothing yet to validate against, so this is
/// our own stand-in — twelve fixed seeds, deliberately not derived from anything per-player (an id, a
/// timestamp), so a guest picking "the fourth swatch" always sends the same seed back and reliably
/// gets the same swatch on their own next load, whatever exact hue that swatch happens to render as.
///
/// Whoever lands #54 is free to replace this list (and the client-side <see cref="IsValid"/> check it
/// enables) with the real palette; until then, this is what the guest editing form on the lobby page
/// limits a guest to picking from, and what it sends as <c>UpdateProfileDto.AvatarSeed</c>.
/// </summary>
public static class AvatarPalette
{
    public static readonly IReadOnlyList<string> Seeds =
        [.. Enumerable.Range(0, 12).Select(i => $"avatar-{i}")];

    public static bool IsValid(string? seed) => seed is not null && Seeds.Contains(seed);
}
