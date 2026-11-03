using Microsoft.JSInterop;

namespace Quesshi.Web.Services;

/// <summary>
/// The app's side of the invite page hand-over. When the app is ready it tells the page, which drops the
/// static preview and reports whether the visitor already pressed join. That intent is spent by the first
/// join attempt, so a seat is asked for once however often the screen is rebuilt.
/// </summary>
public sealed class InviteTakeover(IJSRuntime js)
{
    private bool claimed;
    private bool intent;

    public async Task ReadyAsync()
    {
        if (claimed) return;
        claimed = true;
        try
        {
            intent = await js.InvokeAsync<bool>("quesshi.takeover.claim");
        }
        catch (JSException) { }
    }

    /// <summary>Whether join was pressed before the app was ready and no attempt has used that up yet.</summary>
    public bool TakeIntent()
    {
        var pressed = intent;
        intent = false;
        return pressed;
    }
}
