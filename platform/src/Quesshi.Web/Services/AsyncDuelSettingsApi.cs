using Quesshi.Web.Plugins.Formats.AsyncDuel;

namespace Quesshi.Web.Services;

/// <summary>Backs the async duel settings component with the tenant's category list and the lobby settings endpoint.</summary>
public sealed class AsyncDuelSettingsApi(PlayerApi api) : IAsyncDuelSettingsApi
{
    private sealed record CategoryResponse(Guid Id, string Name, string Icon, string Color);

    public async Task<IReadOnlyList<PickerCategory>> CategoriesAsync(string language, CancellationToken cancellationToken = default)
    {
        var response = await api.GetAsync<List<CategoryResponse>>($"/api/categories?language={Uri.EscapeDataString(language)}", cancellationToken);
        return response.IsSuccess && response.Value is { } categories
            ? [.. categories.Select(category => new PickerCategory(category.Id.ToString(), category.Name, category.Icon, category.Color))]
            : [];
    }

    public async Task<string?> SaveAsync(string sessionId, AsyncDuelSettingsRequest request, CancellationToken cancellationToken = default)
    {
        // A null capacity is left out so the server keeps the lobby's current seat count.
        object body = request.Capacity is { } capacity
            ? new { rules = request.Rules, content = request.Content, capacity }
            : new { rules = request.Rules, content = request.Content };
        var response = await api.SendMutationAsync<object>(
            HttpMethod.Put, $"/api/sessions/{Uri.EscapeDataString(sessionId)}/settings", body, cancellationToken);
        return response.IsSuccess ? null : response.Error ?? "request_failed";
    }
}
