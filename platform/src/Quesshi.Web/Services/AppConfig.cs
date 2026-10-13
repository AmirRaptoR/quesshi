using Quesshi.Web.Models;
using Microsoft.JSInterop;

namespace Quesshi.Web.Services;

/// <summary>Tenant configuration is obtained before the component tree renders.</summary>
public sealed class AppConfig(ClientHttp http, IJSRuntime js)
{
    public TenantConfig Current { get; private set; } = null!;
    public event Action? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Current = await http.GetFromJsonAsync<TenantConfig>("/api/config", cancellationToken)
            ?? throw new InvalidOperationException("The tenant configuration response was empty.");
    }

    public async Task ConfigChanged(long version, CancellationToken cancellationToken = default)
    {
        if (version <= Current.ConfigVersion) return;

        await LoadAsync(cancellationToken);
        await js.InvokeVoidAsync("quesshi.replaceTheme", Current.ConfigVersion);
        Changed?.Invoke();
    }

    public string NameFor(string languageCode) =>
        Current.Languages.FirstOrDefault(language => string.Equals(language.Code, languageCode, StringComparison.OrdinalIgnoreCase))?.AppName
        ?? Current.Name;
}
