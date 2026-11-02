using Microsoft.JSInterop;

namespace Quesshi.Web.Plugins.Formats.Live;

/// <summary>The live screen's cues, played through the shell's browser synthesiser when the player has not muted it.</summary>
public sealed class LiveSounds(IJSRuntime js)
{
    public Task Correct() => PlayAsync("correct");
    public Task Wrong() => PlayAsync("wrong");
    public Task RoundStart() => PlayAsync("roundStart");
    public Task Tick() => PlayAsync("tick");

    private async Task PlayAsync(string cue)
    {
        try
        {
            if (await js.InvokeAsync<string?>("quesshi.get", "quesshi.sounds") == "0") return;
            if (await js.InvokeAsync<bool>("quesshi.sounds.armed")) await js.InvokeVoidAsync("quesshi.sounds.play", cue);
        }
        catch { }
    }
}
