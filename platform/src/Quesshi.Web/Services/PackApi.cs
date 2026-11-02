using System.Text.Json;

namespace Quesshi.Web.Services;

public sealed record PackSummary(Guid Id, string Title, string? Description, string Language, string Visibility);

public sealed record PackQuestion(Guid Id, int Position, string Kind, int KindVersion, string Family, string Language, int? Level, string Prompt, JsonElement Payload, string? Explanation);

/// <summary>The signed-in player's packs and their questions, through <c>/api/packs</c>.</summary>
public sealed class PackApi(PlayerApi api)
{
    public Task<ApiResponse<List<PackSummary>>> ListAsync(CancellationToken cancellationToken = default) =>
        api.GetAsync<List<PackSummary>>("/api/packs", cancellationToken);

    public Task<ApiResponse<PackSummary>> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        api.GetAsync<PackSummary>($"/api/packs/{id}", cancellationToken);

    public Task<ApiResponse<PackSummary>> CreateAsync(string title, string language, CancellationToken cancellationToken = default) =>
        api.SendMutationAsync<PackSummary>(HttpMethod.Post, "/api/packs", new { title, language }, cancellationToken);

    public Task<ApiResponse<PackSummary>> UpdateAsync(Guid id, string? title, string? visibility, CancellationToken cancellationToken = default) =>
        api.SendMutationAsync<PackSummary>(HttpMethod.Put, $"/api/packs/{id}", new { title, visibility }, cancellationToken);

    public Task<ApiResponse<object>> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        api.SendMutationAsync<object>(HttpMethod.Delete, $"/api/packs/{id}", null, cancellationToken);

    public Task<ApiResponse<List<PackQuestion>>> QuestionsAsync(Guid id, CancellationToken cancellationToken = default) =>
        api.GetAsync<List<PackQuestion>>($"/api/packs/{id}/questions", cancellationToken);

    public Task<ApiResponse<PackQuestion>> AddQuestionAsync(Guid id, PackQuestionDraft draft, CancellationToken cancellationToken = default) =>
        api.SendMutationAsync<PackQuestion>(HttpMethod.Post, $"/api/packs/{id}/questions", draft, cancellationToken);

    public Task<ApiResponse<PackQuestion>> UpdateQuestionAsync(Guid id, Guid questionId, PackQuestionDraft draft, CancellationToken cancellationToken = default) =>
        api.SendMutationAsync<PackQuestion>(HttpMethod.Put, $"/api/packs/{id}/questions/{questionId}", draft, cancellationToken);

    public Task<ApiResponse<object>> RemoveQuestionAsync(Guid id, Guid questionId, CancellationToken cancellationToken = default) =>
        api.SendMutationAsync<object>(HttpMethod.Delete, $"/api/packs/{id}/questions/{questionId}", null, cancellationToken);

    public Task<ApiResponse<object>> ReorderAsync(Guid id, IReadOnlyList<Guid> questionIds, CancellationToken cancellationToken = default) =>
        api.SendMutationAsync<object>(HttpMethod.Put, $"/api/packs/{id}/questions/order", new { questionIds }, cancellationToken);
}

/// <summary>The body of a question write.</summary>
public sealed record PackQuestionDraft(string Kind, int KindVersion, string Language, string Prompt, JsonElement Payload, string? Explanation);
