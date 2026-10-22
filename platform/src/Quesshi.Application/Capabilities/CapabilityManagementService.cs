using System.Text.Json;
using Quesshi.Plugin.Api;

namespace Quesshi.Application;

/// <summary>Lists and changes the platform-provisioned capability rows for one tenant.</summary>
internal sealed class CapabilityManagementService(CapabilityRegistry registry, ITenantDb database)
{
    private static readonly JsonSerializerOptions SettingsOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<TenantCapability>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await database.InTenantAsync(tenantId, async transaction =>
        {
            var rows = await transaction.QueryAsync<CapabilityRow>("""
                select type as Type, key as Key, entitled as Entitled, enabled as Enabled, settings as Settings
                from tenant_capabilities
                order by type, key
                """, cancellationToken: cancellationToken);
            return rows
                .Where(row => TryType(row.Type, out var type) && registry.TryGet(type, row.Key) is not null)
                .Select(row => new TenantCapability(row.Type, row.Key, row.Entitled, row.Enabled, row.Settings))
                .ToArray();
        }, cancellationToken: cancellationToken, readOnly: true);

    public async Task<CapabilityManagementResult> UpdateAsync(
        Guid tenantId,
        CapabilityType type,
        string key,
        bool enabled,
        JsonElement settings,
        CancellationToken cancellationToken = default)
    {
        var definition = registry.TryGet(type, key);
        if (definition is null) return CapabilityManagementResult.NotFound;

        object value;
        try
        {
            value = JsonSerializer.Deserialize(settings, definition.SettingsType, SettingsOptions)
                ?? throw new InvalidOperationException("Settings are invalid.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new CapabilityManagementResult(CapabilityManagementStatus.Invalid, ["Settings are invalid."]);
        }

        var messages = definition.ValidateObject(value);
        if (messages.Count > 0) return new CapabilityManagementResult(CapabilityManagementStatus.Invalid, messages);
        var serialized = JsonSerializer.SerializeToElement(value, definition.SettingsType, SettingsOptions);

        return await database.InTenantAsync(tenantId, async transaction =>
        {
            var affected = await transaction.ExecuteAsync("""
                update tenant_capabilities
                set enabled = @enabled, settings = @settings
                where type = @type and key = @key
                """, new { type = ToDatabaseType(type), key, enabled, settings = serialized }, cancellationToken);
            if (affected != 1) return CapabilityManagementResult.NotFound;

            var version = await transaction.QuerySingleOrDefaultAsync<long?>("""
                update tenants set config_version = config_version + 1
                where id = @tenantId
                returning config_version
                """, new { tenantId }, cancellationToken);
            if (version is null) throw new InvalidOperationException($"Tenant {tenantId} does not exist.");
            await transaction.ExecuteAsync("select pg_notify('quesshi_config', @message)",
                new { message = $"config:{tenantId}:{version.Value}" }, cancellationToken);
            return new CapabilityManagementResult(CapabilityManagementStatus.Success);
        }, cancellationToken: cancellationToken);
    }

    private static bool TryType(string value, out CapabilityType type) =>
        Enum.TryParse(value, ignoreCase: true, out type) && Enum.IsDefined(type);

    private static string ToDatabaseType(CapabilityType type) => type.ToString().ToLowerInvariant();

    private sealed record CapabilityRow(string Type, string Key, bool Entitled, bool Enabled, JsonElement Settings);
}

internal sealed record TenantCapability(string Type, string Key, bool Entitled, bool Enabled, JsonElement Settings);

internal enum CapabilityManagementStatus
{
    Success,
    NotFound,
    Invalid
}

internal sealed record CapabilityManagementResult(CapabilityManagementStatus Status, IReadOnlyList<string>? Messages = null)
{
    public static CapabilityManagementResult NotFound { get; } = new(CapabilityManagementStatus.NotFound);
}
