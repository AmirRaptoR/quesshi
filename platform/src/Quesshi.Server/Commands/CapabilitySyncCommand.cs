using Quesshi.Application;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Server.Commands;

internal sealed class CapabilitySyncCommand(ISystemDb database, CapabilityRegistry registry)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var inserted = 0;
        foreach (var definition in registry.Definitions)
        {
            inserted += await database.ExecuteAsync("""
                insert into tenant_capabilities (tenant_id, type, key, entitled, enabled, settings)
                select id, @type, @key, false, false, @settings
                from tenants
                on conflict (tenant_id, type, key) do nothing
                """, new
            {
                type = definition.Type.ToString().ToLowerInvariant(),
                definition.Key,
                settings = definition.SerializeObject(definition.DefaultSettingsObject)
            }, cancellationToken);
        }

        return inserted;
    }
}
