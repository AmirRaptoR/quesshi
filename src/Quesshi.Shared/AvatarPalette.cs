namespace Quesshi.Shared;

/// <summary>
/// The fixed set of avatar seeds a player may pick from — introduced on the lobby page's guest
/// identity editor (issue #53, section 4 of the design doc), and now (issue #54) the palette
/// <c>PUT /api/me</c> validates <see cref="Quesshi.Shared.UpdateProfileDto.AvatarSeed"/> against
/// server-side too, so the client offering a swatch and the server accepting it are checked against
/// the exact same list rather than two lists someone has to remember to keep in sync. It lives here in
/// <c>Quesshi.Shared</c> rather than in <c>Quesshi.Web</c> (where issue #53 first put it) precisely so
/// both sides can reference it: <c>Quesshi.Web</c> already referenced this project for its DTOs, and
/// <c>Quesshi.Application</c> — which <c>Quesshi.Grains</c> and <c>Quesshi.Server</c> both depend on —
/// already referenced it too, so no project gained a new reference to make this shared.
///
/// The doc asks for the seed to be "validated against the palette <c>Ranks.Tint</c> derives colours
/// from" — but <c>Ranks.Tint</c> has no real palette: it hashes any string to one of 360 hues (via
/// <c>string.GetHashCode()</c>, whose seed .NET randomizes per process — so even one fixed seed's
/// exact hue can drift between reloads today, a pre-existing property of every avatar in this app, not
/// something this list changes). There is nothing to validate against there, so this is the
/// project's own stand-in — twelve fixed seeds, deliberately not derived from anything per-player (an
/// id, a timestamp), so a player picking "the fourth swatch" always sends the same seed back and
/// reliably gets the same swatch on their own next load, whatever exact hue that swatch happens to
/// render as.
/// </summary>
public static class AvatarPalette
{
    public static readonly IReadOnlyList<string> Seeds =
        [.. Enumerable.Range(0, 12).Select(i => $"avatar-{i}")];

    public static bool IsValid(string? seed) => seed is not null && Seeds.Contains(seed);
}
