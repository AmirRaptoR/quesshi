using System.Net.Http.Json;
using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>Thin typed wrapper over the endpoints. Returns null on failure; callers show their own message.</summary>
public sealed class Api(HttpClient http)
{
    // --- auth ---
    public Task<AuthConfigDto?> AuthConfigAsync() => GetAsync<AuthConfigDto>("api/auth/config");
    public Task<OtpSentDto?> RequestOtpAsync(string email, string lang) => PostAsync<OtpSentDto>("api/auth/otp/request", new OtpRequestDto(email, lang));
    public Task<AuthResultDto?> VerifyOtpAsync(string email, string code, string lang) => PostAsync<AuthResultDto>("api/auth/otp/verify", new OtpVerifyDto(email, code, lang));
    public Task<AuthResultDto?> GoogleAsync(string idToken, string lang) => PostAsync<AuthResultDto>("api/auth/google", new { idToken, lang });

    // --- invites ---
    public Task<InviteDto?> InviteAsync(string code) => GetAsync<InviteDto>($"api/invite/{Uri.EscapeDataString(Code(code))}");

    public Task<GuestResultDto?> JoinAsGuestAsync(string code, string name, string lang)
        => PostAsync<GuestResultDto>($"api/auth/guest/{Uri.EscapeDataString(Code(code))}", new GuestJoinDto(name, lang));

    private static string Code(string code) => code.Trim().ToUpperInvariant();

    // --- profile ---
    public Task<MeDto?> MeAsync() => GetAsync<MeDto>("api/me");
    public Task<MeDto?> SaveProfileAsync(string displayName, string lang) => PutAsync<MeDto>("api/me", new UpdateProfileDto(displayName, lang));
    public Task<List<CategoryDto>?> CategoriesAsync() => GetAsync<List<CategoryDto>>("api/categories");
    public Task<List<FriendDto>?> SearchPlayersAsync(string q) => GetAsync<List<FriendDto>>($"api/players/search?q={Uri.EscapeDataString(q)}");
    public Task<bool> AddFriendAsync(string id) => SendAsync(HttpMethod.Post, $"api/friends/{id}");
    public Task<bool> RemoveFriendAsync(string id) => SendAsync(HttpMethod.Delete, $"api/friends/{id}");

    // --- play ---
    public Task<MatchSummaryDto?> CreateMatchAsync(bool random, string? lang, List<string>? categories = null,
        int? questions = null, List<int>? levels = null)
        => PostAsync<MatchSummaryDto>("api/matches", new { random, lang, categories, questions, levels });
    public Task<MatchSummaryDto?> JoinAsync(string code) => PostAsync<MatchSummaryDto>($"api/matches/join/{Uri.EscapeDataString(Code(code))}", new { });
    /// <summary>The duels page wants them all; the home page wants only the ones still being played.</summary>
    public Task<List<MatchSummaryDto>?> MatchesAsync(bool activeOnly = false)
        => GetAsync<List<MatchSummaryDto>>(activeOnly ? "api/matches?active=true" : "api/matches");
    public Task<MatchDetailDto?> MatchAsync(string id) => GetAsync<MatchDetailDto>($"api/matches/{id}");
    public Task<AnswerResultDto?> AnswerAsync(string id, int slot, int choice) => PostAsync<AnswerResultDto>($"api/matches/{id}/answer", new { slot, choiceIndex = choice });

    public Task<bool> ReportQuestionAsync(string questionId, string reason)
        => PostJsonAsync("api/report", new ReportQuestionDto(questionId, reason));

    /// <summary>204 means the run is finished; there is no next card.</summary>
    public async Task<QuestionCardDto?> NextAsync(string id)
    {
        var response = await http.PostAsync($"api/matches/{id}/next", null);
        return response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NoContent
            ? await response.Content.ReadFromJsonAsync<QuestionCardDto>()
            : null;
    }

    // --- ranks ---
    public Task<List<LeaderboardRowDto>?> LeaderboardAsync() => GetAsync<List<LeaderboardRowDto>>("api/leaderboard");
    public Task<List<LeaderboardRowDto>?> FriendsBoardAsync() => GetAsync<List<LeaderboardRowDto>>("api/leaderboard/friends");

    // --- plumbing ---
    private async Task<T?> GetAsync<T>(string url)
    {
        try { return await http.GetFromJsonAsync<T>(url); }
        catch { return default; }
    }

    private async Task<T?> PostAsync<T>(string url, object body)
    {
        try
        {
            var response = await http.PostAsJsonAsync(url, body);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>() : default;
        }
        catch { return default; }
    }

    private async Task<T?> PutAsync<T>(string url, object body)
    {
        try
        {
            var response = await http.PutAsJsonAsync(url, body);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>() : default;
        }
        catch { return default; }
    }

    private async Task<T?> PostContentAsync<T>(string url, HttpContent content)
    {
        try
        {
            var response = await http.PostAsync(url, content);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>() : default;
        }
        catch { return default; }
    }

    private async Task<bool> PostJsonAsync(string url, object body)
    {
        try { return (await http.PostAsJsonAsync(url, body)).IsSuccessStatusCode; }
        catch { return false; }
    }

    private async Task<bool> SendAsync(HttpMethod method, string url)
    {
        try { return (await http.SendAsync(new HttpRequestMessage(method, url))).IsSuccessStatusCode; }
        catch { return false; }
    }
}
