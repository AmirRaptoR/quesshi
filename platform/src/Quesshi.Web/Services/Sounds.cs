using Microsoft.JSInterop;

namespace Quesshi.Web.Services;

/// <summary>Browser-synthesised sounds retained from the current client shell.</summary>
public sealed class Sounds(IJSRuntime js)
{
    public Task Correct() => PlayAsync("correct");
    public Task Wrong() => PlayAsync("wrong");
    public Task RoundStart() => PlayAsync("roundStart");
    public Task Tick() => PlayAsync("tick");
    public Task Win() => PlayAsync("win");
    public Task Loss() => PlayAsync("loss");

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
