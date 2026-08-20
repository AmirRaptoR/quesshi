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

    /// <summary>The change-password page is the one screen a must-change admin may still see.</summary>
    protected virtual bool AllowWhilePasswordChangePending => false;

    protected override async Task OnInitializedAsync()
    {
        Admin.Changed += StateHasChanged;

        // The panel reads the same translation table as the game, so switching language has to
        // redraw the page body, not just the nav that holds the button.
        State.Changed += StateHasChanged;

        await Admin.InitialiseAsync();

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

    protected virtual Task LoadAsync() => Task.CompletedTask;

    public virtual void Dispose()
    {
        Admin.Changed -= StateHasChanged;
        State.Changed -= StateHasChanged;
    }
}
