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
    /// A token is held but the startup identity call couldn't confirm it. The page renders a retry
    /// state instead of its own content — and instead of the sign-in wall, since the token may still
    /// be good.
    /// </summary>
    protected bool Unconfirmed;

    /// <summary>
    /// Whether a guest may open this page. Default is no, so a page added later is closed to them
    /// until someone decides otherwise — the same way the API gate works.
    /// </summary>
    protected virtual bool AllowsGuest => false;

    protected override async Task OnInitializedAsync()
    {
        State.Changed += StateHasChanged;
        await State.InitialiseAsync();
        await EnterAsync();
    }

    /// <summary>
    /// The guard every entry into the page runs through: on first load and again after a retry from
    /// the unconfirmed state. Never navigates away while a retained token might still be good.
    /// </summary>
    private async Task EnterAsync()
    {
        if (State.Unconfirmed)
        {
            Unconfirmed = true;
            return;
        }

        Unconfirmed = false;

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

    /// <summary>Re-runs the identity call from the unconfirmed state, without losing the page or its URL.</summary>
    protected async Task RetrySessionAsync()
    {
        await State.RetryIdentityAsync();
        await EnterAsync();
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    public virtual void Dispose() => State.Changed -= StateHasChanged;
}
