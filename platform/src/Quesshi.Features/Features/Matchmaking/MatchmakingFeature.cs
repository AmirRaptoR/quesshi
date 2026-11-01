using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Features.Matchmaking;

public sealed class MatchmakingFeature : IFeature
{
    public const string SweepJob = "sweep";
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    public const string WaitingItemType = "matchmaking.waiting";

    public string Id => "matchmaking";

    public FeatureSettings Settings { get; } = FeatureSettings.Create(new MatchmakingSettings(), static _ => []);

    public void Configure(IFeatureBuilder feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        feature.PlayerRoutes(MapPlayerRoutes);
        feature.CleanupPlayer(CleanupPlayerAsync);
        feature.Job(SweepJob, SweepAsync, WhenDisabled.Cancel);
        feature.Every(SweepInterval, SweepJob);
        feature.Inbox(GetInboxItemsAsync);
    }

    private static void MapPlayerRoutes(RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("", EnqueueAsync).AllowGuests();
        routes.MapDelete("/{format}", LeaveAsync).AllowGuests();
    }

    private static async Task<IResult> EnqueueAsync(
        EnqueueRequest request,
        HttpContext context,
        ITenantDb database,
        ILocks locks,
        ISessions sessions,
        IInbox inbox,
        CancellationToken cancellationToken)
    {
        if (!TryGetPlayer(context.User, out var playerId, out var tenantId)) return Results.Unauthorized();
        // Join-waiting formats (async duel) arrive with the follow-up; until then they are refused.
        if (request.Format is null || !MatchmakingRules.IsCreateStarted(request.Format))
            return Results.BadRequest(new { error = "unsupported_format" });

        var outcome = await new MatchmakingQueue(database, locks, sessions, inbox)
            .EnqueueAsync(tenantId, playerId, request.Format, request.Settings, cancellationToken);
        return outcome switch
        {
            EnqueueOutcome.Queued => Results.Ok(),
            EnqueueOutcome.InvalidSettings => Results.BadRequest(new { error = "invalid_settings" }),
            EnqueueOutcome.FormatUnavailable => Results.BadRequest(new { error = "format_unavailable" }),
            EnqueueOutcome.AlreadyPlaying => Results.Conflict(new { error = "already_playing" }),
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> LeaveAsync(
        string format,
        HttpContext context,
        ITenantDb database,
        ILocks locks,
        ISessions sessions,
        IInbox inbox,
        CancellationToken cancellationToken)
    {
        if (!TryGetPlayer(context.User, out var playerId, out var tenantId)) return Results.Unauthorized();
        await new MatchmakingQueue(database, locks, sessions, inbox).LeaveAsync(tenantId, playerId, format, cancellationToken);
        return Results.Ok();
    }

    private static Task SweepAsync(JobContext context, CancellationToken cancellationToken)
    {
        if (context.TenantId is not { } tenantId || context.Database is null || context.Services is null)
            throw new InvalidOperationException("The matchmaking sweep is a tenant job.");
        var services = context.Services;
        return new MatchmakingQueue(
            context.Database,
            services.GetRequiredService<ILocks>(),
            services.GetRequiredService<ISessions>(),
            services.GetRequiredService<IInbox>()).SweepAsync(tenantId, cancellationToken);
    }

    private static async Task<IReadOnlyList<InboxItem>> GetInboxItemsAsync(
        ITenantTx transaction, Guid playerId, CancellationToken cancellationToken)
    {
        var waiting = await transaction.QueryAsync<WaitingItem>("""
            select format as Format, enqueued_at as EnqueuedAt
            from matchmaking_entries
            where player_id = @playerId
            order by enqueued_at, format
            """, new { playerId }, cancellationToken);
        return waiting
            .Select(item => new InboxItem(WaitingItemType, JsonSerializer.SerializeToElement(item, MatchmakingJsonContext.Default.WaitingItem)))
            .ToArray();
    }

    private static Task CleanupPlayerAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("delete from matchmaking_entries where player_id = @playerId", new { playerId }, cancellationToken);

    private static bool TryGetPlayer(ClaimsPrincipal user, out Guid playerId, out Guid tenantId)
    {
        playerId = Guid.Empty;
        tenantId = Guid.Empty;
        return Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out playerId)
            && Guid.TryParse(user.FindFirstValue("tenant"), out tenantId);
    }

    private sealed record EnqueueRequest(string? Format, JsonElement Settings);
}

public sealed record MatchmakingSettings;
