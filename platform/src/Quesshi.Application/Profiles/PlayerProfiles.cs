using Quesshi.Plugin.Api;

namespace Quesshi.Application.Profiles;

/// <summary>Tenant-owned player profile data and validation shared by guest entry and profile edits.</summary>
internal static class PlayerProfiles
{
    public static bool TryNormalizeDisplayName(string? value, out string displayName)
    {
        displayName = value?.Trim() ?? string.Empty;
        return displayName.Length is >= 2 and <= 24;
    }

    public static async Task<PlayerProfile> CreateGuestAsync(
        ITenantDb tenantDb,
        Guid tenantId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var playerId = Guid.NewGuid();
        return await tenantDb.InTenantAsync(tenantId, async transaction =>
        {
            var language = await transaction.QuerySingleOrDefaultAsync<string>("""
                select default_language from tenants
                """, cancellationToken: cancellationToken);
            if (language is null) throw new InvalidOperationException("The tenant is not available.");

            var profile = await transaction.QuerySingleOrDefaultAsync<PlayerProfile>("""
                insert into players (tenant_id, id, account_id, display_name, avatar_seed, lang)
                values (@tenantId, @playerId, null, @displayName, @avatarSeed, @language)
                returning id as Id, display_name as DisplayName, avatar_seed as AvatarSeed, lang as Lang
                """, new { tenantId, playerId, displayName, avatarSeed = PlayerAvatarPalette.Default, language }, cancellationToken);
            return profile ?? throw new InvalidOperationException("The guest profile could not be created.");
        }, cancellationToken: cancellationToken);
    }

    public static Task<PlayerProfile?> GetAsync(
        ITenantDb tenantDb,
        Guid tenantId,
        Guid playerId,
        CancellationToken cancellationToken = default) =>
        tenantDb.InTenantAsync(tenantId, transaction => transaction.QuerySingleOrDefaultAsync<PlayerProfile>("""
            select id as Id, display_name as DisplayName, avatar_seed as AvatarSeed, lang as Lang
            from players
            where id = @playerId and deleted_at is null
            """, new { playerId }, cancellationToken), cancellationToken: cancellationToken, readOnly: true);

    public static async Task<ProfileUpdateResult> UpdateAsync(
        ITenantDb tenantDb,
        Guid tenantId,
        Guid playerId,
        string? displayName,
        string? avatarSeed,
        string? language,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeDisplayName(displayName, out var normalizedDisplayName)
            || (avatarSeed is not null && !PlayerAvatarPalette.IsValid(avatarSeed))
            || string.IsNullOrWhiteSpace(language))
        {
            return ProfileUpdateResult.Invalid;
        }

        return await tenantDb.InTenantAsync(tenantId, async transaction =>
        {
            var offeredLanguage = await transaction.QuerySingleOrDefaultAsync<string>("""
                select code from tenant_languages where code = @language
                """, new { language }, cancellationToken);
            if (offeredLanguage is null) return ProfileUpdateResult.Invalid;

            var profile = await transaction.QuerySingleOrDefaultAsync<PlayerProfile>("""
                update players
                set display_name = @normalizedDisplayName, avatar_seed = coalesce(@avatarSeed, avatar_seed), lang = @language
                where id = @playerId and deleted_at is null
                returning id as Id, display_name as DisplayName, avatar_seed as AvatarSeed, lang as Lang
                """, new { playerId, normalizedDisplayName, avatarSeed, language }, cancellationToken);
            return profile is null ? ProfileUpdateResult.Missing : new ProfileUpdateResult(profile);
        }, cancellationToken: cancellationToken);
    }
}

internal sealed record PlayerProfile(Guid Id, string DisplayName, string AvatarSeed, string Lang);

internal enum ProfileUpdateStatus
{
    Updated,
    Invalid,
    Missing
}

internal sealed record ProfileUpdateResult(ProfileUpdateStatus Status, PlayerProfile? Profile)
{
    public static readonly ProfileUpdateResult Invalid = new(ProfileUpdateStatus.Invalid, null);
    public static readonly ProfileUpdateResult Missing = new(ProfileUpdateStatus.Missing, null);
    public ProfileUpdateResult(PlayerProfile profile) : this(ProfileUpdateStatus.Updated, profile) { }
}

/// <summary>The platform counterpart of the established twelve-seed avatar palette.</summary>
internal static class PlayerAvatarPalette
{
    public const string Default = "avatar-0";
    private static readonly string[] Seeds = [.. Enumerable.Range(0, 12).Select(index => $"avatar-{index}")];

    public static bool IsValid(string? seed) => seed is not null && Seeds.Contains(seed, StringComparer.Ordinal);
}
