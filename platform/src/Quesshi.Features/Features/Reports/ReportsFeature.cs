using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Features.Reports;

public sealed class ReportsFeature : IFeature
{
    private const int HistoryLimit = 60;
    private const int SuppressionThreshold = 3;

    public string Id => "reports";

    public FeatureSettings Settings { get; } = FeatureSettings.Create(new ReportsSettings(), static _ => []);

    public void Configure(IFeatureBuilder feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        feature.PlayerRoutes(MapPlayerRoutes);
        feature.AdminRoutes(AdminRole.Moderator, ReportsModerationRoutes.MapAdminRoutes);
        feature.CleanupPlayer(CleanupPlayerAsync);
    }

    private static void MapPlayerRoutes(RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("", SubmitAsync);
    }

    private static async Task<IResult> SubmitAsync(
        ReportRequest request,
        HttpContext context,
        ITenantDb database,
        ILocks locks,
        IContent content,
        CancellationToken cancellationToken)
    {
        if (!TryGetPlayer(context.User, out var playerId, out var tenantId)) return Results.Unauthorized();
        if (!Enum.TryParse<ReportReason>(request.Reason, true, out var reason) || !Enum.IsDefined(reason))
            return Results.BadRequest(new { error = "bad_reason" });

        var outcome = await database.InTenantAsync(tenantId, async transaction =>
        {
            await locks.ContentAsync(transaction, [request.QuestionId], cancellationToken);
            var eligible = await transaction.QuerySingleOrDefaultAsync<bool?>("""
                select exists (
                    select 1
                    from (
                        select participant.tenant_id, participant.session_id
                        from session_participants participant
                        join sessions session on session.tenant_id = participant.tenant_id
                                             and session.id = participant.session_id
                        where participant.player_id = @playerId
                          and session.status in ('finished', 'cancelled')
                        order by session.created_at desc, session.id desc
                        limit @historyLimit
                    ) history
                    join session_items item on item.tenant_id = history.tenant_id
                                          and item.session_id = history.session_id
                    where item.question_id = @questionId
                )
                """, new { playerId, request.QuestionId, historyLimit = HistoryLimit }, cancellationToken);
            if (eligible is not true) return ReportOutcome.NotYourQuestion;

            var question = await content.GetAsync(transaction, request.QuestionId, cancellationToken);
            if (question is null) return ReportOutcome.NotYourQuestion;

            var inserted = await transaction.QuerySingleOrDefaultAsync<int?>("""
                insert into question_reports (tenant_id, question_id, player_id, reason, note)
                values (@tenantId, @questionId, @playerId, @reason, @note)
                on conflict do nothing
                returning 1
                """, new
            {
                tenantId,
                questionId = request.QuestionId,
                playerId,
                reason = reason.ToString(),
                request.Note,
            }, cancellationToken);
            var reportCount = await transaction.QuerySingleOrDefaultAsync<long>("""
                select count(*)
                from question_reports
                where question_id = @questionId
                """, new { questionId = request.QuestionId }, cancellationToken);
            if (reportCount >= SuppressionThreshold && question.Status is QuestionStatus.Approved)
                await content.SetStatusAsync(transaction, request.QuestionId, QuestionStatus.Pending, cancellationToken);

            return inserted is null ? ReportOutcome.AlreadyReported : ReportOutcome.Reported;
        }, cancellationToken: cancellationToken);

        return outcome switch
        {
            ReportOutcome.NotYourQuestion => Results.BadRequest(new { error = "not_your_question" }),
            ReportOutcome.Reported => Results.Ok(new { reported = true, alreadyReported = false }),
            ReportOutcome.AlreadyReported => Results.Ok(new { reported = true, alreadyReported = true }),
            _ => throw new InvalidOperationException($"Unknown report outcome: {outcome}."),
        };
    }

    private static Task CleanupPlayerAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("""
            delete from question_reports
            where player_id = @playerId
            """, new { playerId }, cancellationToken);

    private static bool TryGetPlayer(ClaimsPrincipal user, out Guid playerId, out Guid tenantId)
    {
        playerId = Guid.Empty;
        tenantId = Guid.Empty;
        return Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out playerId)
            && Guid.TryParse(user.FindFirstValue("tenant"), out tenantId);
    }

    private sealed record ReportRequest(Guid QuestionId, string? Reason, string? Note);

    private sealed record ReportsSettings;

    private enum ReportReason
    {
        WrongAnswer,
        Unclear,
        Duplicate,
        Offensive,
        Other,
    }

    private enum ReportOutcome
    {
        NotYourQuestion,
        Reported,
        AlreadyReported,
    }
}
