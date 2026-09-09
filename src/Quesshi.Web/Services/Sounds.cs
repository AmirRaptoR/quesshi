using Microsoft.JSInterop;

namespace Quesshi.Web.Services;

/// <summary>
/// Six short cues — correct, wrong, round start, the last-three-seconds tick, win, loss — synthesised
/// on the client with the Web Audio API (see <c>window.quesshi.sounds</c> in <c>index.html</c>), so
/// they still play offline in the PWA with no audio assets to ship. Every cue is a fire-and-forget
/// <see cref="Task"/> a page can call from an event handler without awaiting or wrapping in its own
/// try/catch: both gates below fail closed, so a call that cannot reach JS, or arrives before the app
/// is ready to make sound, is a silent no-op rather than a thrown exception.
/// <para>
/// Both gates live here, in plain C#, rather than inside the JS module — precisely so a test can
/// substitute a fake <see cref="IJSRuntime"/> and exercise them without a browser:
/// </para>
/// <list type="bullet">
/// <item>
/// the mute switch — Settings (#91) writes the localStorage key <c>"quesshi.sounds"</c> ("1"/"0",
/// default on) directly, with no event Blazor is ever told about, so this re-reads it on every single
/// call rather than caching a value from whenever the service happened to be constructed;
/// </item>
/// <item>
/// the autoplay gate — a fresh <c>AudioContext</c> starts suspended until the page's own first
/// pointerdown or keydown resumes it (wired once, entirely in JS, in <c>index.html</c>), so a cue
/// fired before that gesture asks JS "are you armed yet" — a cheap boolean read, not audio work — and
/// stops there rather than reaching the actual play call that touches the context.
/// </item>
/// </list>
/// </summary>
public sealed class Sounds(IJSRuntime js)
{
    /// <summary>Same key, same "1"/"0"/default-on convention Settings (#91) writes.</summary>
    private const string MuteKey = "quesshi.sounds";

    /// <summary>Two rising notes — the reveal for a round the local player got right.</summary>
    public Task Correct() => PlayAsync("correct");

    /// <summary>A single low buzz — the reveal for a round the local player got wrong.</summary>
    public Task Wrong() => PlayAsync("wrong");

    /// <summary>A soft tick — a fresh round card has just arrived.</summary>
    public Task RoundStart() => PlayAsync("roundStart");

    /// <summary>A short click — once per second, for each of the round clock's last three seconds.</summary>
    public Task Tick() => PlayAsync("tick");

    /// <summary>A three-note ascending fanfare — the duel resolved in the local player's favour.</summary>
    public Task Win() => PlayAsync("win");

    /// <summary>A descending pair — the duel resolved against the local player.</summary>
    public Task Loss() => PlayAsync("loss");

    /// <summary>
    /// One entry point for every cue, so the mute and autoplay gates are each enforced exactly once
    /// rather than repeated six times over. Swallows any interop failure the same way this app's other
    /// localStorage calls do (see <c>AppState</c>) — a torn-down circuit or a browser without Web Audio
    /// should never turn a sound effect into a crashed page.
    /// </summary>
    private async Task PlayAsync(string cue)
    {
        try
        {
            var muted = await js.InvokeAsync<string?>("quesshi.get", MuteKey) == "0";
            if (muted) return;

            if (!await js.InvokeAsync<bool>("quesshi.sounds.armed")) return;

            await js.InvokeVoidAsync("quesshi.sounds.play", cue);
        }
        catch { /* JS interop unavailable — no sound, no crash */ }
    }
}
