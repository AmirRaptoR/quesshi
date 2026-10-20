namespace Quesshi.Web.Admin;

public enum AdminSessionStatus { Loading, SignedOut, SignedIn, Unconfirmed }

public sealed class AdminState(AdminApi api)
{
    public AdminSessionStatus Status { get; private set; } = AdminSessionStatus.Loading;
    public AdminSession? Session { get; private set; }
    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await api.CurrentAsync(cancellationToken);
        Session = result.Value;
        Status = result.Success ? AdminSessionStatus.SignedIn : result.Error == "admin.auth.signedOut" ? AdminSessionStatus.SignedOut : AdminSessionStatus.Unconfirmed;
        Changed?.Invoke();
    }

    public async Task<AdminResult<LoginResult>> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var result = await api.LoginAsync(username, password, cancellationToken);
        if (result.Success) await InitializeAsync(cancellationToken);
        return result;
    }

    public Task<AdminResult<object>> ForgotAsync(string usernameOrEmail, CancellationToken cancellationToken = default) => api.ForgotAsync(usernameOrEmail, cancellationToken);

    public Task<AdminResult<object>> ResetAsync(string token, string password, CancellationToken cancellationToken = default) => api.ResetAsync(token, password, cancellationToken);

    public async Task<AdminResult<object>> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        var result = await api.ChangePasswordAsync(currentPassword, newPassword, cancellationToken);
        if (result.Success) await InitializeAsync(cancellationToken);
        return result;
    }

    public Task<AdminResult<IReadOnlyList<AdminAccount>>> AccountsAsync(CancellationToken cancellationToken = default) => api.AccountsAsync(cancellationToken);
    public Task<AdminResult<AdminAccount>> CreateAsync(string username, string email, string password, CancellationToken cancellationToken = default) => api.CreateAsync(username, email, password, cancellationToken);
    public Task<AdminResult<object>> SetMembershipActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default) => api.SetMembershipActiveAsync(id, active, cancellationToken);
    public Task<AdminResult<T>> MutateAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken = default) => api.MutateAsync<T>(method, path, body, cancellationToken);
    public Task<AdminResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken = default) => api.GetAsync<T>(path, cancellationToken);
    public Task<AdminResult<T>> MutateContentAsync<T>(HttpMethod method, string path, Func<HttpContent> contentFactory, CancellationToken cancellationToken = default) => api.MutateContentAsync<T>(method, path, contentFactory, cancellationToken);

    public async Task<AdminResult<object>> SignOutAsync(CancellationToken cancellationToken = default)
    {
        var result = await api.SignOutAsync(cancellationToken);
        if (!result.Success) return result;
        Session = null;
        Status = AdminSessionStatus.SignedOut;
        Changed?.Invoke();
        return result;
    }
}
