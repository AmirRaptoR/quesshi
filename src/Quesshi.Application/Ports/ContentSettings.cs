namespace Quesshi.Application.Ports;

/// <summary>Per-tenant category allowlists for each game family.</summary>
public sealed record ContentSettings(IReadOnlyList<string> TriviaCategoryIds, IReadOnlyList<string> VotingCategoryIds)
{
    public static ContentSettings Empty { get; } = new([], []);
}

public interface IContentSettingsRepository
{
    Task<ContentSettings> GetAsync(CancellationToken ct = default);
    Task<ContentSettings> GetOrCreateAsync(ContentSettings initial, CancellationToken ct = default);
    Task SaveAsync(ContentSettings settings, CancellationToken ct = default);
}
