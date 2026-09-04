using Microsoft.AspNetCore.Components;

namespace Quesshi.Web.Services;

/// <summary>
/// Base for every page inside the admin panel. It has nothing to do with <see cref="PageBase"/>:
/// being signed in to play grants no access here, and an unauthenticated visitor is sent to the
/// admin sign-in page rather than to the game.
/// </summary>
public abstract class AdminPageBase : ComponentBase, IDisposable
{
    [Inject] protected AdminState Admin { get; set; } = default!;
    [Inject] protected AppState State { get; set; } = default!;
    [Inject] protected AdminApi Api { get; set; } = default!;
    [Inject] protected Translator L { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected bool Loading = true;

    /// <summary>
    /// A token is held but the startup identity call couldn't confirm it. The page renders a retry
    /// state instead of its own content — and instead of the sign-in wall, since the token may still
    /// be good.
    /// </summary>
    protected bool Unconfirmed;

    /// <summary>The change-password page is the one screen a must-change admin may still see.</summary>
    protected virtual bool AllowWhilePasswordChangePending => false;

    protected override async Task OnInitializedAsync()
    {
        Admin.Changed += StateHasChanged;

        // The panel reads the same translation table as the game, so switching language has to
        // redraw the page body, not just the nav that holds the button.
        State.Changed += StateHasChanged;

        await Admin.InitialiseAsync();
        await EnterAsync();
    }

    /// <summary>
    /// The guard every entry into the page runs through: on first load and again after a retry from
    /// the unconfirmed state. Never navigates away while a retained token might still be good.
    /// </summary>
    private async Task EnterAsync()
    {
        if (Admin.Unconfirmed)
        {
            Unconfirmed = true;
            return;
        }

        Unconfirmed = false;

        if (!Admin.SignedIn)
        {
            Nav.NavigateTo($"/admin/login?next={Uri.EscapeDataString(new Uri(Nav.Uri).PathAndQuery)}");
            return;
        }

        if (Admin.Admin!.MustChangePassword && !AllowWhilePasswordChangePending)
        {
            Nav.NavigateTo("/admin/password");
            return;
        }

        await LoadAsync();
        Loading = false;
    }

    /// <summary>Re-runs the identity call from the unconfirmed state, without losing the page or its URL.</summary>
    protected async Task RetrySessionAsync()
    {
        await Admin.RetryIdentityAsync();
        await EnterAsync();
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    public virtual void Dispose()
    {
        Admin.Changed -= StateHasChanged;
        State.Changed -= StateHasChanged;
    }
}
