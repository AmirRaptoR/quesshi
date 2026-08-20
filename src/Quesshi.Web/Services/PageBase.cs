using Microsoft.AspNetCore.Components;

namespace Quesshi.Web.Services;

/// <summary>Every page behind the sign-in wall shares the same three dependencies and the same guard.</summary>
public abstract class PageBase : ComponentBase, IDisposable
{
    [Inject] protected AppState State { get; set; } = default!;
    [Inject] protected Api Api { get; set; } = default!;
    [Inject] protected Translator L { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected bool Loading = true;

    /// <summary>
    /// Whether a guest may open this page. Default is no, so a page added later is closed to them
    /// until someone decides otherwise — the same way the API gate works.
    /// </summary>
    protected virtual bool AllowsGuest => false;

    protected override async Task OnInitializedAsync()
    {
        State.Changed += StateHasChanged;
        await State.InitialiseAsync();

        if (!State.SignedIn)
        {
            // Carry where they were headed, so signing in lands them there instead of the lobby.
            var here = Nav.ToBaseRelativePath(Nav.Uri);
            Nav.NavigateTo($"/signin?next={Uri.EscapeDataString("/" + here)}");
            return;
        }

        // Belt to the API's braces: the server refuses a guest anyway, this keeps them from
        // watching a page fail to load before it does.
        if (State.IsGuest && !AllowsGuest)
        {
            Nav.NavigateTo(State.GuestMatchId is { Length: > 0 } id ? $"/duel/{id}" : "/signin");
            return;
        }

        await LoadAsync();
        Loading = false;
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    public virtual void Dispose() => State.Changed -= StateHasChanged;
}
