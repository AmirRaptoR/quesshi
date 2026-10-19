using Quesshi.Application.Media;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;

namespace Quesshi.Server.Media;

/// <summary>Weekly tenant job that removes old, unreferenced immutable media files and rows.</summary>
internal sealed class MediaCleanup(IMediaStore store, IClock clock)
{
    public async Task HandleAsync(JobContext context, CancellationToken cancellationToken)
    {
        var database = context.Database ?? throw new InvalidOperationException("Media cleanup jobs require a tenant.");
        var tenantId = context.TenantId ?? throw new InvalidOperationException("Media cleanup jobs require a tenant.");
        var cutoff = clock.Now - TimeSpan.FromDays(7);
        var candidates = await database.InTenantAsync(tenantId, transaction => transaction.QueryAsync<Guid>("""
            select id
            from media_assets asset
            where created_at < @cutoff
              and not exists (select 1 from questions where media_id = asset.id)
              and not exists (select 1 from session_items where media_id = asset.id)
            order by id
            """, new { cutoff }, cancellationToken), cancellationToken: cancellationToken);
        foreach (var id in candidates)
        {
            await store.DeleteAsync(tenantId, id, cancellationToken);
        }
    }
}
