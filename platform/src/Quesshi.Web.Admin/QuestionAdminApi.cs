using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Quesshi.Web.Services;

namespace Quesshi.Web.Admin;

/// <summary>Question-specific mutation client that retains field validation returned by the API.</summary>
internal sealed class QuestionAdminApi(ClientHttp http)
{
    private string? antiforgery;

    public async Task<QuestionApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        try { return QuestionApiResult<T>.FromValue(await http.GetFromJsonAsync<T>(path, cancellationToken)); }
        catch (HttpRequestException) { return QuestionApiResult<T>.Failure("load_failed"); }
    }

    public Task<QuestionApiResult<QuestionResponse>> CreateAsync(QuestionWrite request, CancellationToken cancellationToken = default) => SendAsync<QuestionResponse>(HttpMethod.Post, "/api/admin/questions", request, cancellationToken);
    public Task<QuestionApiResult<QuestionResponse>> UpdateAsync(Guid id, QuestionWrite request, CancellationToken cancellationToken = default) => SendAsync<QuestionResponse>(HttpMethod.Put, $"/api/admin/questions/{id}", request, cancellationToken);
    public Task<QuestionApiResult<object>> ApproveAsync(Guid id, CancellationToken cancellationToken = default) => SendAsync<object>(HttpMethod.Post, $"/api/admin/questions/{id}/approve", null, cancellationToken);
    public Task<QuestionApiResult<object>> RejectAsync(Guid id, CancellationToken cancellationToken = default) => SendAsync<object>(HttpMethod.Post, $"/api/admin/questions/{id}/reject", null, cancellationToken);
    public Task<QuestionApiResult<object>> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => SendAsync<object>(HttpMethod.Delete, $"/api/admin/questions/{id}", null, cancellationToken);
    public Task<QuestionApiResult<object>> BulkApproveAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) => SendAsync<object>(HttpMethod.Post, "/api/admin/questions/approve", new { ids }, cancellationToken);

    private async Task<QuestionApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            var token = await AntiforgeryAsync(cancellationToken);
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-Admin", token);
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return response.StatusCode == HttpStatusCode.NoContent
                    ? QuestionApiResult<T>.FromValue(default)
                    : QuestionApiResult<T>.FromValue(await response.Content.ReadFromJsonAsync<T>(cancellationToken));
            var failure = await response.Content.ReadFromJsonAsync<QuestionFailure>(cancellationToken);
            return new QuestionApiResult<T>(false, default, failure?.Error ?? $"http_{(int)response.StatusCode}", failure?.Problems ?? []);
        }
        catch (HttpRequestException)
        {
            return QuestionApiResult<T>.Failure("request_failed");
        }
    }

    private async Task<string> AntiforgeryAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(antiforgery)) return antiforgery;
        var token = await http.GetFromJsonAsync<AntiforgeryToken>("/api/admin/antiforgery", cancellationToken) ?? throw new HttpRequestException("Missing antiforgery token.");
        antiforgery = token.Token;
        return antiforgery;
    }

    private sealed record AntiforgeryToken(string Token);
    private sealed record QuestionFailure(string? Error, IReadOnlyList<QuestionProblem>? Problems);
}

internal sealed record QuestionApiResult<T>(bool Success, T? Value, string? Error, IReadOnlyList<QuestionProblem> Problems)
{
    public static QuestionApiResult<T> FromValue(T? value) => new(true, value, null, []);
    public static QuestionApiResult<T> Failure(string error) => new(false, default, error, []);
}

internal sealed record QuestionProblem(string Code, string Message);
internal sealed record QuestionWrite(string Kind, int KindVersion, string Family, string Language, Guid? CategoryId, int? Level, string Prompt, System.Text.Json.JsonElement Payload, string? Explanation, string? Topic, string? Status, string Source, Guid? OwnerId, Guid? MediaId);
public sealed record QuestionResponse(Guid Id, string Kind, int KindVersion, string Family, string Language, Guid? CategoryId, int? Level, string Prompt, System.Text.Json.JsonElement Payload, string? Explanation, string? Topic, string Status, string Source, Guid? OwnerId, Guid? MediaId);
