using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Quesshi.Plugin.Api;

namespace Quesshi.Application.Content;

/// <summary>Tenant-owned category content and its language-aware display projection.</summary>
internal sealed class CategoryService(ITenantDb database) : ICategoryService
{
    public Task<IReadOnlyList<CategoryDefinition>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        database.InTenantAsync<IReadOnlyList<CategoryDefinition>>(tenantId, async transaction =>
            (await transaction.QueryAsync<PersistedCategory>("""
                select id as Id, slug as Slug, names::text as NamesJson, icon as Icon, color as Color,
                       is_active as IsActive, sort_order as SortOrder
                from categories
                order by sort_order, id
                """, cancellationToken: cancellationToken)).Select(ToDefinition).ToArray(),
            cancellationToken: cancellationToken, readOnly: true);

    public Task<IReadOnlyList<CategoryDisplay>> ListActiveAsync(
        Guid tenantId,
        string? family,
        string? language,
        CancellationToken cancellationToken = default) =>
        database.InTenantAsync<IReadOnlyList<CategoryDisplay>>(tenantId, async transaction =>
        {
            var defaultLanguage = await transaction.QuerySingleOrDefaultAsync<string>("""
                select default_language from tenants
                """, cancellationToken: cancellationToken);
            if (defaultLanguage is null) return Array.Empty<CategoryDisplay>();

            var categories = await transaction.QueryAsync<PersistedCategory>("""
                select id as Id, slug as Slug, names::text as NamesJson, icon as Icon, color as Color,
                       is_active as IsActive, sort_order as SortOrder
                from categories
                where is_active
                order by sort_order, id
                """, cancellationToken: cancellationToken);
            var allowed = family is null
                ? null
                : (await ReadBankSettingsAsync(transaction, cancellationToken)).CategoriesByFamily.TryGetValue(family, out var ids)
                    ? ids.ToHashSet()
                    : [];
            return categories
                .Where(category => allowed is null || allowed.Contains(category.Id))
                .Select(category => ToDisplay(category, language, defaultLanguage))
                .ToArray();
        }, cancellationToken: cancellationToken, readOnly: true);

    public Task<BankCategorySettings> GetBankSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, transaction => ReadBankSettingsAsync(transaction, cancellationToken),
            cancellationToken: cancellationToken, readOnly: true);

    public async Task<BankCategorySettingsChangeResult> SetBankSettingsAsync(
        Guid tenantId,
        IReadOnlyDictionary<string, IReadOnlyList<Guid>>? categoriesByFamily,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeBankSettings(categoriesByFamily, out var normalized)) return BankCategorySettingsChangeResult.Invalid;

        return await database.InTenantAsync(tenantId, async transaction =>
        {
            var ids = normalized.Values.SelectMany(ids => ids).Distinct().ToArray();
            var existing = ids.Length == 0
                ? 0L
                : await transaction.QuerySingleOrDefaultAsync<long>("select count(*) from categories where id = any(@ids)",
                    new { ids }, cancellationToken);
            if (existing != ids.Length) return BankCategorySettingsChangeResult.Invalid;

            var settings = new BankCategorySettings(normalized);
            var updated = await transaction.ExecuteAsync("""
                update tenant_capabilities
                set settings = @settings
                where type = 'source' and key = 'bank'
                """, new
            {
                settings = new Jsonb<BankCategorySettings>(settings, CategoryJsonContext.Default.BankCategorySettings),
            }, cancellationToken);
            if (updated != 1) return BankCategorySettingsChangeResult.Missing;

            var version = await transaction.QuerySingleOrDefaultAsync<long?>("""
                update tenants
                set config_version = config_version + 1
                where id = @tenantId
                returning config_version
                """, new { tenantId }, cancellationToken);
            if (version is null) return BankCategorySettingsChangeResult.Missing;
            await transaction.ExecuteAsync("select pg_notify('quesshi_config', @message)",
                new { message = $"config:{tenantId}:{version.Value}" }, cancellationToken);
            return BankCategorySettingsChangeResult.Success;
        }, cancellationToken: cancellationToken);
    }

    public async Task<CategoryChangeResult> CreateAsync(
        Guid tenantId,
        CategoryWrite request,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(request, out var normalized)) return CategoryChangeResult.Invalid;

        try
        {
            return await database.InTenantAsync(tenantId, async transaction =>
            {
                if (!await NamesAreValidAsync(transaction, normalized.Names, cancellationToken)) return CategoryChangeResult.Invalid;
                var category = await transaction.QuerySingleOrDefaultAsync<PersistedCategory>("""
                    insert into categories (tenant_id, id, slug, names, icon, color, is_active, sort_order)
                    values (@tenantId, @id, @slug, @names, @icon, @color, @isActive, @sortOrder)
                    returning id as Id, slug as Slug, names::text as NamesJson, icon as Icon, color as Color,
                              is_active as IsActive, sort_order as SortOrder
                    """, new
                {
                    tenantId,
                    id = Guid.NewGuid(),
                    normalized.Slug,
                    names = new Jsonb<Dictionary<string, string>>(normalized.Names, CategoryJsonContext.Default.DictionaryStringString),
                    normalized.Icon,
                    normalized.Color,
                    isActive = normalized.IsActive ?? true,
                    normalized.SortOrder,
                }, cancellationToken);
                return category is null ? CategoryChangeResult.Failed : new CategoryChangeResult(ToDefinition(category));
            }, cancellationToken: cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return CategoryChangeResult.DuplicateSlug;
        }
    }

    public async Task<CategoryChangeResult> UpdateAsync(
        Guid tenantId,
        Guid id,
        CategoryWrite request,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(request, out var normalized)) return CategoryChangeResult.Invalid;

        try
        {
            return await database.InTenantAsync(tenantId, async transaction =>
            {
                if (!await NamesAreValidAsync(transaction, normalized.Names, cancellationToken)) return CategoryChangeResult.Invalid;
                var category = await transaction.QuerySingleOrDefaultAsync<PersistedCategory>("""
                    update categories
                    set slug = @slug, names = @names, icon = @icon, color = @color,
                        is_active = coalesce(@isActive, is_active), sort_order = @sortOrder
                    where id = @id
                    returning id as Id, slug as Slug, names::text as NamesJson, icon as Icon, color as Color,
                              is_active as IsActive, sort_order as SortOrder
                    """, new
                {
                    id,
                    normalized.Slug,
                    names = new Jsonb<Dictionary<string, string>>(normalized.Names, CategoryJsonContext.Default.DictionaryStringString),
                    normalized.Icon,
                    normalized.Color,
                    normalized.IsActive,
                    normalized.SortOrder,
                }, cancellationToken);
                return category is null ? CategoryChangeResult.Missing : new CategoryChangeResult(ToDefinition(category));
            }, cancellationToken: cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return CategoryChangeResult.DuplicateSlug;
        }
    }

    public Task<CategoryChangeResult> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, async transaction =>
        {
            var changed = await transaction.ExecuteAsync("""
                update categories set is_active = false where id = @id and is_active
                """, new { id }, cancellationToken);
            return changed == 0 ? CategoryChangeResult.Missing : CategoryChangeResult.Success;
        }, cancellationToken: cancellationToken);

    public Task<CategoryChangeResult> DeleteAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, async transaction =>
        {
            var category = await transaction.QuerySingleOrDefaultAsync<Guid?>("select id from categories where id = @id for update", new { id }, cancellationToken);
            if (category is null) return CategoryChangeResult.Missing;

            var inUse = await transaction.QuerySingleOrDefaultAsync<int?>("select 1 from questions where category_id = @id limit 1", new { id }, cancellationToken);
            if (inUse is not null) return CategoryChangeResult.InUse;

            return await transaction.ExecuteAsync("delete from categories where id = @id", new { id }, cancellationToken) == 1
                ? CategoryChangeResult.Success
                : CategoryChangeResult.Failed;
        }, cancellationToken: cancellationToken);

    public Task<CategoryChangeResult> ReorderAsync(
        Guid tenantId,
        IReadOnlyList<Guid>? ids,
        CancellationToken cancellationToken = default)
    {
        if (ids is null || ids.Count == 0 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            return Task.FromResult(CategoryChangeResult.Invalid);

        return database.InTenantAsync(tenantId, async transaction =>
        {
            var existing = await transaction.QuerySingleOrDefaultAsync<long>("""
                select count(*) from categories where id = any(@ids)
                """, new { ids = ids.ToArray() }, cancellationToken);
            if (existing != ids.Count) return CategoryChangeResult.Missing;

            await transaction.ExecuteAsync("""
                update categories
                set sort_order = (requested.position - 1)::integer
                from unnest(@ids::uuid[]) with ordinality as requested(id, position)
                where categories.id = requested.id
                """, new { ids = ids.ToArray() }, cancellationToken);
            return CategoryChangeResult.Success;
        }, cancellationToken: cancellationToken);
    }

    private static async Task<bool> NamesAreValidAsync(
        ITenantTx transaction,
        Dictionary<string, string> names,
        CancellationToken cancellationToken)
    {
        var defaultLanguage = await transaction.QuerySingleOrDefaultAsync<string>("""
            select default_language from tenants
            """, cancellationToken: cancellationToken);
        if (defaultLanguage is null || !names.ContainsKey(defaultLanguage)) return false;

        var offeredLanguages = (await transaction.QueryAsync<string>("""
            select code from tenant_languages
            """, cancellationToken: cancellationToken)).ToHashSet(StringComparer.Ordinal);
        return names.Keys.All(offeredLanguages.Contains);
    }

    private static async Task<BankCategorySettings> ReadBankSettingsAsync(ITenantTx transaction, CancellationToken cancellationToken)
    {
        var json = await transaction.QuerySingleOrDefaultAsync<JsonElement?>("""
            select settings
            from tenant_capabilities
            where type = 'source' and key = 'bank'
            """, cancellationToken: cancellationToken);
        var settings = json is null
            ? null
            : JsonSerializer.Deserialize(json.Value, CategoryJsonContext.Default.BankCategorySettings);
        return settings is { CategoriesByFamily: not null }
            ? new(settings.CategoriesByFamily)
            : new(new Dictionary<string, Guid[]>(StringComparer.Ordinal));
    }

    private static bool TryNormalizeBankSettings(
        IReadOnlyDictionary<string, IReadOnlyList<Guid>>? categoriesByFamily,
        out Dictionary<string, Guid[]> normalized)
    {
        normalized = new(StringComparer.Ordinal);
        if (categoriesByFamily is null) return false;

        foreach (var (family, ids) in categoriesByFamily)
        {
            if (!BankCategoryFamilies.IsKnown(family) || ids is null || ids.Any(id => id == Guid.Empty))
                return false;
            var copied = ids.Distinct().ToArray();
            if (copied.Length != ids.Count || !normalized.TryAdd(family, copied)) return false;
        }
        return true;
    }

    private static bool TryNormalize(CategoryWrite request, out NormalizedCategoryWrite normalized)
    {
        normalized = default!;
        var slug = NormalizeSlug(request.Slug);
        var icon = request.Icon?.Trim() ?? string.Empty;
        var color = request.Color?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(slug) || request.Names is null || request.Names.Count == 0)
            return false;

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, name) in request.Names)
        {
            var normalizedCode = code?.Trim();
            var normalizedName = name?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedCode) || string.IsNullOrWhiteSpace(normalizedName)
                || !names.TryAdd(normalizedCode, normalizedName))
                return false;
        }

        normalized = new(slug, names, icon, color, request.IsActive, request.SortOrder);
        return true;
    }

    private static string? NormalizeSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var dashed = new string([.. value.Trim().ToLowerInvariant().Select(character => char.IsWhiteSpace(character) ? '-' : character)]);
        var slug = new string([.. dashed.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')]);
        return slug.Trim('-');
    }

    private static CategoryDefinition ToDefinition(PersistedCategory category) => new(
        category.Id,
        category.Slug,
        JsonSerializer.Deserialize(category.NamesJson, CategoryJsonContext.Default.DictionaryStringString)
            ?? throw new InvalidOperationException("A category has invalid names."),
        category.Icon,
        category.Color,
        category.IsActive,
        category.SortOrder);

    private static CategoryDisplay ToDisplay(PersistedCategory category, string? language, string defaultLanguage)
    {
        var definition = ToDefinition(category);
        return new(definition.Id, definition.Slug, definition.NameFor(language, defaultLanguage), definition.Icon, definition.Color, definition.SortOrder);
    }

    private sealed record PersistedCategory(Guid Id, string Slug, string NamesJson, string Icon, string Color, bool IsActive, int SortOrder);
    private sealed record NormalizedCategoryWrite(string Slug, Dictionary<string, string> Names, string Icon, string Color, bool? IsActive, int SortOrder);
}

internal interface ICategoryService
{
    Task<IReadOnlyList<CategoryDefinition>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CategoryDisplay>> ListActiveAsync(Guid tenantId, string? family, string? language, CancellationToken cancellationToken = default);
    Task<BankCategorySettings> GetBankSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<BankCategorySettingsChangeResult> SetBankSettingsAsync(Guid tenantId, IReadOnlyDictionary<string, IReadOnlyList<Guid>>? categoriesByFamily, CancellationToken cancellationToken = default);
    Task<CategoryChangeResult> CreateAsync(Guid tenantId, CategoryWrite request, CancellationToken cancellationToken = default);
    Task<CategoryChangeResult> UpdateAsync(Guid tenantId, Guid id, CategoryWrite request, CancellationToken cancellationToken = default);
    Task<CategoryChangeResult> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);
    Task<CategoryChangeResult> DeleteAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);
    Task<CategoryChangeResult> ReorderAsync(Guid tenantId, IReadOnlyList<Guid>? ids, CancellationToken cancellationToken = default);
}

internal sealed record CategoryDefinition(
    Guid Id,
    string Slug,
    IReadOnlyDictionary<string, string> Names,
    string Icon,
    string Color,
    bool IsActive,
    int SortOrder)
{
    /// <summary>Returns the requested language when available, otherwise the tenant default.</summary>
    public string NameFor(string? language, string defaultLanguage)
    {
        var requested = language?.Trim();
        return !string.IsNullOrEmpty(requested) && Names.TryGetValue(requested, out var name)
            ? name
            : Names[defaultLanguage];
    }
}

internal sealed record CategoryDisplay(Guid Id, string Slug, string Name, string Icon, string Color, int SortOrder);

internal sealed record CategoryWrite(string? Slug, IReadOnlyDictionary<string, string>? Names, string? Icon, string? Color, int SortOrder, bool? IsActive = null);

internal static class BankCategoryFamilies
{
    public static readonly IReadOnlyList<string> All = ["trivia", "poll"];

    public static bool IsKnown(string? value) => !string.IsNullOrWhiteSpace(value)
        && All.Contains(value, StringComparer.Ordinal);
}

internal sealed record BankCategorySettings([property: JsonPropertyName("categoriesByFamily")] Dictionary<string, Guid[]> CategoriesByFamily);

internal enum BankCategorySettingsChangeStatus
{
    Success,
    Invalid,
    Missing,
}

internal sealed record BankCategorySettingsChangeResult(BankCategorySettingsChangeStatus Status)
{
    public static readonly BankCategorySettingsChangeResult Success = new(BankCategorySettingsChangeStatus.Success);
    public static readonly BankCategorySettingsChangeResult Invalid = new(BankCategorySettingsChangeStatus.Invalid);
    public static readonly BankCategorySettingsChangeResult Missing = new(BankCategorySettingsChangeStatus.Missing);
}

internal enum CategoryChangeStatus
{
    Success,
    Invalid,
    DuplicateSlug,
    InUse,
    Missing,
    Failed,
}

internal sealed record CategoryChangeResult(CategoryChangeStatus Status, CategoryDefinition? Category = null)
{
    public static readonly CategoryChangeResult Success = new(CategoryChangeStatus.Success);
    public static readonly CategoryChangeResult Invalid = new(CategoryChangeStatus.Invalid);
    public static readonly CategoryChangeResult DuplicateSlug = new(CategoryChangeStatus.DuplicateSlug);
    public static readonly CategoryChangeResult InUse = new(CategoryChangeStatus.InUse);
    public static readonly CategoryChangeResult Missing = new(CategoryChangeStatus.Missing);
    public static readonly CategoryChangeResult Failed = new(CategoryChangeStatus.Failed);
    public CategoryChangeResult(CategoryDefinition category) : this(CategoryChangeStatus.Success, category) { }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(BankCategorySettings))]
internal partial class CategoryJsonContext : JsonSerializerContext;
