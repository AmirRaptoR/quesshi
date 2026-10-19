using System.Text.Json;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Sources.Bank;

/// <summary>Draws approved, tenant-owned bank questions using the legacy question-set rules.</summary>
public sealed class BankSource : ISource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const string SourceId = "bank";

    public string Id => SourceId;

    public Type Params => typeof(BankParams);

    public FeatureSettings Settings { get; } = FeatureSettings.Create(
        new BankSettings(new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.Ordinal)),
        ValidateSettings);

    public Task<bool> AuthorizeAsync(ITenantTx transaction, JsonElement parameters, Guid actor, CancellationToken cancellationToken = default) =>
        IsAvailableAsync(transaction, cancellationToken);

    public async Task<Picked> PickAsync(ITenantTx transaction, JsonElement parameters, SourceDrawPlan plan, Guid actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(plan);
        if (!await IsAvailableAsync(transaction, cancellationToken)) throw new SourceUnavailableException();
        if (plan.Count <= 0) throw new SourceParameterException("The draw count must be positive.");
        if (string.IsNullOrWhiteSpace(plan.Language)) throw new SourceParameterException("The draw language is required.");
        if (plan.AcceptedKinds.Count == 0) throw new SourceParameterException("At least one accepted kind is required.");

        var request = ReadParameters(parameters);
        var kinds = SelectKinds(plan.AcceptedKinds, request.Kinds);
        var levels = SelectLevels(plan.Levels, request.Levels);
        var settings = await ReadSettingsAsync(transaction, cancellationToken);
        var permittedCategories = kinds
            .SelectMany(kind => settings.CategoriesByFamily.TryGetValue(kind.Family, out var ids) ? ids : [])
            .Distinct()
            .ToArray();
        var categories = await SelectCategoriesAsync(transaction, request.Categories, permittedCategories, plan.Seed, cancellationToken);
        if (categories.Count == 0) throw new NotEnoughQuestionsException(0, plan.Count);

        var used = new List<Guid>(plan.Count);
        for (var slot = 0; slot < plan.Count; slot++)
        {
            var level = LevelForSlot(slot, plan.Count, levels);
            var preferred = categories[slot % categories.Count];
            var id = await SampleAsync(transaction, plan.Language, KindsForCategory(kinds, settings, preferred), preferred, level, used, plan.Seed + slot, cancellationToken);
            if (id is null)
            {
                foreach (var category in categories.Where(category => category != preferred))
                {
                    id = await SampleAsync(transaction, plan.Language, KindsForCategory(kinds, settings, category), category, level, used, plan.Seed + slot, cancellationToken);
                    if (id is not null) break;
                }
            }
            if (id is null)
            {
                foreach (var fallback in levels.Where(fallback => fallback != level))
                {
                    foreach (var category in categories)
                    {
                        id = await SampleAsync(transaction, plan.Language, KindsForCategory(kinds, settings, category), category, fallback, used, plan.Seed + slot, cancellationToken);
                        if (id is not null) break;
                    }
                    if (id is not null) break;
                }
            }
            if (id is null) throw new NotEnoughQuestionsException(used.Count, plan.Count);
            used.Add(id.Value);
        }
        return new PickedQuestionIds(used);
    }

    private static async Task<bool> IsAvailableAsync(ITenantTx transaction, CancellationToken cancellationToken) =>
        await transaction.QuerySingleOrDefaultAsync<bool?>("""
            select entitled and enabled
            from tenant_capabilities
            where type = 'source' and key = 'bank'
            """, cancellationToken: cancellationToken) == true;

    private static async Task<BankSettings> ReadSettingsAsync(ITenantTx transaction, CancellationToken cancellationToken)
    {
        var settings = await transaction.QuerySingleOrDefaultAsync<JsonElement?>("""
            select settings from tenant_capabilities where type = 'source' and key = 'bank'
            """, cancellationToken: cancellationToken);
        if (settings is null) throw new SourceUnavailableException();
        return JsonSerializer.Deserialize<BankSettings>(settings.Value, JsonOptions) ?? throw new SourceParameterException("The bank settings are invalid.");
    }

    private static async Task<IReadOnlyList<Guid>> SelectCategoriesAsync(ITenantTx transaction, IReadOnlyList<Guid>? requested, Guid[] allowed, long seed, CancellationToken cancellationToken)
    {
        var requestedIds = requested?.Distinct().ToArray() ?? [];
        if (requestedIds.Length > 3) throw new SourceParameterException("At most three categories may be selected.");
        var rows = await transaction.QueryAsync<Guid>("""
            select categories.id
            from categories
            left join unnest(@requested) with ordinality as requested(id, ordinal)
                on categories.id = requested.id
            where categories.is_active and categories.id = any(@allowed)
              and (cardinality(@requested) = 0 or categories.id = any(@requested))
            order by case when cardinality(@requested) = 0 then hashtextextended(categories.id::text, @seed) else 0 end,
                     requested.ordinal,
                     categories.id
            limit 3
            """, new { allowed, requested = requestedIds, seed }, cancellationToken);
        if (requestedIds.Length > 0 && rows.Count != requestedIds.Length)
            throw new SourceParameterException("Each requested category must be active and allowed for the selected kind family.");
        return rows;
    }

    private static async Task<Guid?> SampleAsync(ITenantTx transaction, string language, IReadOnlyList<SourceKind> kinds, Guid category, int level, IReadOnlyList<Guid> used, long seed, CancellationToken cancellationToken) =>
        await transaction.QuerySingleOrDefaultAsync<Guid?>("""
            select id
            from questions
            where status = 'approved'
              and owner_id is null
              and lang = @language
              and kind = any(@kinds)
              and category_id = @category
              and level = @level
              and not (id = any(@used))
            order by hashtextextended(id::text, @seed), id
            limit 1
            """, new
        {
            language,
            kinds = kinds.Select(kind => kind.Id).ToArray(),
            category,
            level,
            used = used.ToArray(),
            seed
        }, cancellationToken);

    private static IReadOnlyList<SourceKind> KindsForCategory(IReadOnlyList<SourceKind> kinds, BankSettings settings, Guid category) =>
        kinds.Where(kind => settings.CategoriesByFamily.TryGetValue(kind.Family, out var categories) && categories.Contains(category)).ToArray();

    private static BankParams ReadParameters(JsonElement parameters)
    {
        if (parameters.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new();
        return JsonSerializer.Deserialize<BankParams>(parameters, JsonOptions) ?? throw new SourceParameterException("The bank parameters are invalid.");
    }

    private static IReadOnlyList<SourceKind> SelectKinds(IReadOnlyList<SourceKind> accepted, IReadOnlyList<string>? requested)
    {
        var allowed = accepted.DistinctBy(kind => kind.Id, StringComparer.Ordinal).ToArray();
        if (requested is not { Count: > 0 }) return allowed;
        var selected = requested.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Any(id => allowed.All(kind => kind.Id != id))) throw new SourceParameterException("A requested kind is not accepted by the plan.");
        return allowed.Where(kind => selected.Contains(kind.Id, StringComparer.Ordinal)).ToArray();
    }

    private static IReadOnlyList<int> SelectLevels(IReadOnlyList<int>? planLevels, IReadOnlyList<int>? requested)
    {
        var available = (planLevels is { Count: > 0 } ? planLevels : [1, 2, 3, 4, 5]).Distinct().Order().ToArray();
        var selected = (requested is { Count: > 0 } ? requested : available).Distinct().Order().ToArray();
        if (selected.Any(level => level is < 1 or > 5 || !available.Contains(level))) throw new SourceParameterException("A requested level is not accepted by the plan.");
        return selected;
    }

    private static int LevelForSlot(int slot, int total, IReadOnlyList<int> levels)
    {
        if (total <= 1 || levels.Count == 1) return levels[levels.Count / 2];
        var index = (int)Math.Round((levels.Count - 1.0) * slot / (total - 1), MidpointRounding.AwayFromZero);
        return levels[Math.Clamp(index, 0, levels.Count - 1)];
    }

    private static IReadOnlyList<string> ValidateSettings(BankSettings settings)
    {
        if (settings.CategoriesByFamily is null) return ["CategoriesByFamily is required."];
        var errors = new List<string>();
        foreach (var (family, categories) in settings.CategoriesByFamily)
        {
            if (string.IsNullOrWhiteSpace(family)) errors.Add("A kind family is required.");
            if (categories is null) errors.Add($"The allow-list for '{family}' is required.");
            else if (categories.Distinct().Count() != categories.Count) errors.Add($"The allow-list for '{family}' contains duplicate categories.");
        }
        return errors;
    }
}

public sealed record BankParams(IReadOnlyList<Guid>? Categories = null, IReadOnlyList<int>? Levels = null, IReadOnlyList<string>? Kinds = null);

public sealed record BankSettings(IReadOnlyDictionary<string, IReadOnlyList<Guid>> CategoriesByFamily);
