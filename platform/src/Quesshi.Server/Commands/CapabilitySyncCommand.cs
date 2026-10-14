using Quesshi.Application;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Server.Commands;

internal sealed class CapabilitySyncCommand(ISystemDb database, CapabilityRegistry registry)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        return await database.InTransactionAsync(async transaction =>
        {
            var inserted = 0;
            var affectedTenantIds = new HashSet<Guid>();
            foreach (var definition in registry.Definitions)
            {
                var tenantIds = await transaction.QueryAsync<Guid>("""
                    insert into tenant_capabilities (tenant_id, type, key, entitled, enabled, settings)
                    select id, @type, @key, false, false, @settings
                    from tenants
                    on conflict (tenant_id, type, key) do nothing
                    returning tenant_id
                    """, new
                {
                    type = definition.Type.ToString().ToLowerInvariant(),
                    definition.Key,
                    settings = definition.SerializeObject(definition.DefaultSettingsObject)
                }, cancellationToken);

                inserted += tenantIds.Count;
                affectedTenantIds.UnionWith(tenantIds);
            }

            if (affectedTenantIds.Count > 0)
            {
                var versions = await transaction.QueryAsync<TenantConfigVersion>("""
                    update tenants
                    set config_version = config_version + 1
                    where id = any(@tenantIds)
                    returning id as TenantId, config_version as Version
                    """, new { tenantIds = affectedTenantIds.ToArray() }, cancellationToken);
                foreach (var version in versions)
                {
                    await transaction.ExecuteAsync("select pg_notify('quesshi_config', @message)",
                        new { message = $"config:{version.TenantId}:{version.Version}" }, cancellationToken);
                }
            }

            return inserted;
        }, cancellationToken);
    }

    private sealed record TenantConfigVersion(Guid TenantId, long Version);
}
