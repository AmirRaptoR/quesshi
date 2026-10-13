using System.Text.Json;
using Quesshi.Application;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Capabilities;

internal sealed class TenantCapabilities(CapabilityRegistry registry, ITenantDb database) : ICapabilities
{
    public async Task<bool> IsOnAsync(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default)
    {
        if (registry.TryGet(type, key) is null) return false;

        return await database.InTenantAsync(tenantId, async transaction =>
        {
            var row = await transaction.QuerySingleOrDefaultAsync<CapabilityState>("""
                select entitled as Entitled, enabled as Enabled, settings as Settings
                from tenant_capabilities
                where type = @type and key = @key
                """, new { type = ToDatabaseType(type), key }, cancellationToken);
            return row is { Entitled: true, Enabled: true };
        }, cancellationToken: cancellationToken);
    }

    public async Task<TSettings> GetSettingsAsync<TSettings>(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default)
        where TSettings : notnull
    {
        var definition = registry.Get(type, key);
        if (definition.SettingsType != typeof(TSettings))
        {
            throw new InvalidOperationException($"Capability {type}:{key} does not use {typeof(TSettings).Name} settings.");
        }

        return await database.InTenantAsync(tenantId, async transaction =>
        {
            var row = await transaction.QuerySingleOrDefaultAsync<CapabilitySettings>("""
                select settings as Settings
                from tenant_capabilities
                where type = @type and key = @key
                """, new { type = ToDatabaseType(type), key }, cancellationToken);
            if (row is null) throw new InvalidOperationException($"Capability {type}:{key} is not provisioned for this tenant.");
            return (TSettings)definition.Deserialize(row.Settings);
        }, cancellationToken: cancellationToken);
    }

    public async Task SetAsync<TSettings>(Guid tenantId, CapabilityType type, string key, bool enabled, TSettings settings, CancellationToken cancellationToken = default)
        where TSettings : notnull
    {
        var definition = registry.Get(type, key);
        var messages = definition.ValidateObject(settings);
        if (messages.Count > 0) throw new CapabilityValidationException(messages);
        var serializedSettings = definition.SerializeObject(settings);

        await database.InTenantAsync(tenantId, async transaction =>
        {
            var affected = await transaction.ExecuteAsync("""
                update tenant_capabilities
                set enabled = @enabled, settings = @settings
                where type = @type and key = @key
                """, new { type = ToDatabaseType(type), key, enabled, settings = serializedSettings }, cancellationToken);
            if (affected != 1) throw new InvalidOperationException($"Capability {type}:{key} is not provisioned for this tenant.");

            await transaction.ExecuteAsync("update tenants set config_version = config_version + 1", cancellationToken: cancellationToken);
            return 0;
        }, cancellationToken: cancellationToken);
    }

    private static string ToDatabaseType(CapabilityType type) => type.ToString().ToLowerInvariant();

    private sealed record CapabilityState(bool Entitled, bool Enabled, JsonElement Settings);

    private sealed record CapabilitySettings(JsonElement Settings);
}
