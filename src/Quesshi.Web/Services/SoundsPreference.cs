namespace Quesshi.Web.Services;

/// <summary>
/// The one preference Settings.razor owns that never touches the server: whether the #93 sounds
/// service plays anything. Read and written through the same <c>window.quesshi.get</c>/<c>set</c>
/// localStorage helpers every other client-only flag in this app already uses (see
/// <c>AppState</c>'s own <c>TokenKey</c>/<c>LangKey</c>) — deliberately not folded into
/// <c>AppState</c> or <c>PUT /api/me</c>, because the sounds service reads this exact key on its own,
/// independent of whether a player is signed in or what page they are on; two independent readers of
/// one literal string is simpler than wiring an event both sides would need to subscribe to.
///
/// The stored value is <c>"1"</c> or <c>"0"</c> — the same convention <c>AppState.GuestMatchLiveKey</c>
/// already uses for a boolean flag — rather than <c>"true"</c>/<c>"false"</c>, so both readers agree on
/// the exact literal without also needing to agree on a boolean parser.
/// </summary>
public static class SoundsPreference
{
    /// <summary>Must match the #93 sounds service's own key exactly — this is the one contract between
    /// the two features, and it is a bare string precisely so neither side needs a reference to the
    /// other's code to honour it.</summary>
    public const string Key = "quesshi.sounds";

    /// <summary>Missing entirely — nobody has ever touched this control — means on: silence should
    /// never be the unannounced default a fresh install starts in.</summary>
    public static bool Parse(string? stored) => stored != "0";

    public static string Serialize(bool enabled) => enabled ? "1" : "0";
}
