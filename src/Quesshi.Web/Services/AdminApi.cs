using System.Net.Http.Json;
using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// Every call the admin panel makes, on the admin HttpClient and therefore the admin token.
/// A 401 here means the admin session expired, never that the game session did.
/// </summary>
public sealed class AdminApi(AdminHttpClient http)
{
    private HttpClient Client => http.Client;

    // --- sign in -------------------------------------------------------------------
    public Task<AdminSessionDto?> LoginAsync(string username, string password)
        => PostAsync<AdminSessionDto>("api/admin/auth/login", new AdminLoginDto(username, password));

    public Task<AdminForgotSentDto?> ForgotAsync(string usernameOrEmail)
        => PostAsync<AdminForgotSentDto>("api/admin/auth/forgot", new AdminForgotDto(usernameOrEmail));

    public Task<AdminAuthErrorDto?> ResetAsync(string token, string newPassword)
        => PostForErrorAsync("api/admin/auth/reset", new AdminResetDto(token, newPassword));

    public Task<AdminAuthErrorDto?> ChangePasswordAsync(string current, string next)
        => PostForErrorAsync("api/admin/auth/password", new AdminChangePasswordDto(current, next));

    public Task<AdminIdentityDto?> MeAsync() => GetAsync<AdminIdentityDto>("api/admin/auth/me");

    public Task<AdminAuthErrorDto?> ChangeEmailAsync(string email)
        => PostForErrorAsync("api/admin/auth/email", new AdminEmailDto(email));

    /// <summary>The login endpoint answers with a reason and, when locked out, until when.</summary>
    public async Task<(AdminSessionDto? Session, AdminAuthErrorDto? Error)> TryLoginAsync(string username, string password)
    {
        try
        {
            var response = await Client.PostAsJsonAsync("api/admin/auth/login", new AdminLoginDto(username, password));

            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<AdminSessionDto>(), null)
                : (null, await response.Content.ReadFromJsonAsync<AdminAuthErrorDto>());
        }
        catch
        {
            return (null, new AdminAuthErrorDto("common.error"));
        }
    }

    // --- accounts ------------------------------------------------------------------
    public Task<List<AdminAccountDto>?> AccountsAsync() => GetAsync<List<AdminAccountDto>>("api/admin/accounts");
    public Task<AdminAuthErrorDto?> CreateAccountAsync(CreateAdminDto body) => PostForErrorAsync("api/admin/accounts", body);
    public Task<bool> SetAccountActiveAsync(string id, bool value) => SendAsync(HttpMethod.Post, $"api/admin/accounts/{id}/active?value={value}");
    public Task<bool> DeleteAccountAsync(string id) => SendAsync(HttpMethod.Delete, $"api/admin/accounts/{id}");

    // --- the panel -----------------------------------------------------------------
    public Task<AdminDashboardDto?> DashboardAsync() => GetAsync<AdminDashboardDto>("api/admin/dashboard");
    public Task<AdminQuestionPageDto?> ReportedAsync(int skip, int take)
        => GetAsync<AdminQuestionPageDto>($"api/admin/reported?skip={skip}&take={take}");

    public Task<AdminQuestionDto?> DismissReportsAsync(string id)
        => PostAsync<AdminQuestionDto>($"api/admin/questions/{id}/dismiss-reports", new { });

    public Task<AdminQuestionPageDto?> AdminQuestionsAsync(string query) => GetAsync<AdminQuestionPageDto>($"api/admin/questions?{query}");
    public Task<AdminQuestionDto?> SaveQuestionAsync(SaveQuestionDto body) => PostAsync<AdminQuestionDto>("api/admin/questions", body);
    public Task<bool> ApproveAsync(string id) => SendAsync(HttpMethod.Post, $"api/admin/questions/{id}/approve");
    public Task<bool> RejectAsync(string id) => SendAsync(HttpMethod.Post, $"api/admin/questions/{id}/reject");
    public Task<bool> DeleteQuestionAsync(string id) => SendAsync(HttpMethod.Delete, $"api/admin/questions/{id}");
    public Task<List<CategoryDto>?> AdminCategoriesAsync() => GetAsync<List<CategoryDto>>("api/admin/categories");
    public Task<bool> SaveCategoryAsync(CategoryDto body) => PostJsonAsync("api/admin/categories", body);
    public Task<bool> DeleteCategoryAsync(string id) => SendAsync(HttpMethod.Delete, $"api/admin/categories/{id}");
    public Task<AdminUserPageDto?> AdminUsersAsync(string? q) => GetAsync<AdminUserPageDto>($"api/admin/users?q={Uri.EscapeDataString(q ?? "")}");
    public Task<bool> SetBannedAsync(string id, bool value) => SendAsync(HttpMethod.Post, $"api/admin/users/{id}/ban?value={value}");
    public Task<GenerationRunDto?> GenerateNowAsync() => PostAsync<GenerationRunDto>("api/admin/generate", new { });
    public Task<GenerationRunDto?> GenerateBucketAsync(GenerateRequestDto body) => PostAsync<GenerationRunDto>("api/admin/generate/bucket", body);
    public Task<GenerationRunDto?> GenerateIllustratedAsync(GenerateRequestDto body) => PostAsync<GenerationRunDto>("api/admin/generate/illustrated", body);
    public Task<MediaDto?> UploadAsync(MultipartFormDataContent content) => PostContentAsync<MediaDto>("api/admin/media", content);

    // --- plumbing ------------------------------------------------------------------
    private async Task<T?> GetAsync<T>(string url)
    {
        try { return await Client.GetFromJsonAsync<T>(url); }
        catch { return default; }
    }

    private async Task<T?> PostAsync<T>(string url, object body)
    {
        try
        {
            var response = await Client.PostAsJsonAsync(url, body);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>() : default;
        }
        catch { return default; }
    }

    /// <summary>Null means it worked; anything else is the reason it did not.</summary>
    private async Task<AdminAuthErrorDto?> PostForErrorAsync(string url, object body)
    {
        try
        {
            var response = await Client.PostAsJsonAsync(url, body);
            return response.IsSuccessStatusCode ? null : await response.Content.ReadFromJsonAsync<AdminAuthErrorDto>();
        }
        catch { return new AdminAuthErrorDto("common.error"); }
    }

    private async Task<T?> PostContentAsync<T>(string url, HttpContent content)
    {
        try
        {
            var response = await Client.PostAsync(url, content);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>() : default;
        }
        catch { return default; }
    }

    private async Task<bool> PostJsonAsync(string url, object body)
    {
        try { return (await Client.PostAsJsonAsync(url, body)).IsSuccessStatusCode; }
        catch { return false; }
    }

    private async Task<bool> SendAsync(HttpMethod method, string url)
    {
        try { return (await Client.SendAsync(new HttpRequestMessage(method, url))).IsSuccessStatusCode; }
        catch { return false; }
    }
}
