using Quesshi.Domain;

namespace Quesshi.Application.UseCases;

/// <summary>Selects a match's categories from the active family allowlist.</summary>
public static class CategorySelection
{
    public static IReadOnlyList<string> Select(IEnumerable<Category> eligible, IReadOnlyList<string>? playerPicks)
    {
        var active = eligible.Where(c => c.IsActive).ToList();
        var byId = active.ToDictionary(c => c.Id, StringComparer.Ordinal);
        if (playerPicks is { Count: > 0 })
            return [.. playerPicks.Distinct(StringComparer.Ordinal).Where(byId.ContainsKey)
                .Take(MatchRules.CategoriesPerMatch)];
        return [.. active.OrderBy(_ => Random.Shared.Next()).Take(MatchRules.CategoriesPerMatch).Select(c => c.Id)];
    }
}
