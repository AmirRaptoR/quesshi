using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Features.Reports;

internal static class ReportsModerationRoutes
{
    public static void MapAdminRoutes(RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("", ListAsync);
        routes.MapPost("/{questionId:guid}/dismiss", DismissAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        ITenantRequestContext tenants,
        ITenantDb database,
        CancellationToken cancellationToken)
    {
        if (tenants.GetTenantId(context) is not { } tenantId) return Results.NotFound();

        var reports = await database.InTenantAsync(
            tenantId,
            transaction => ListReportsAsync(transaction, cancellationToken),
            cancellationToken: cancellationToken,
            readOnly: true);
        return Results.Ok(reports);
    }

    private static async Task<IResult> DismissAsync(
        Guid questionId,
        HttpContext context,
        ITenantRequestContext tenants,
        ITenantDb database,
        ILocks locks,
        IContent content,
        CancellationToken cancellationToken)
    {
        if (tenants.GetTenantId(context) is not { } tenantId) return Results.NotFound();

        var dismissed = await database.InTenantAsync(
            tenantId,
            transaction => DismissReportsAsync(transaction, questionId, locks, content, cancellationToken),
            cancellationToken: cancellationToken);
        return dismissed ? Results.Ok() : Results.NotFound();
    }

    private static async Task<IReadOnlyList<ReportedQuestion>> ListReportsAsync(
        ITenantTx transaction,
        CancellationToken cancellationToken)
    {
        var rows = await transaction.QueryAsync<ReportReasonRow>("""
            select question_id as QuestionId,
                   count(*) over (partition by question_id)::bigint as Count,
                   reason as Reason
            from question_reports
            order by count(*) over (partition by question_id) desc, question_id, created_at, player_id
            """, cancellationToken: cancellationToken);
        return rows.GroupBy(static row => (row.QuestionId, row.Count))
            .Select(static group => new ReportedQuestion(
                group.Key.QuestionId,
                group.Key.Count,
                group.Select(static row => row.Reason).ToArray()))
            .ToArray();
    }

    private static async Task<bool> DismissReportsAsync(
        ITenantTx transaction,
        Guid questionId,
        ILocks locks,
        IContent content,
        CancellationToken cancellationToken)
    {
        await locks.ContentAsync(transaction, [questionId], cancellationToken);
        if (await content.GetAsync(transaction, questionId, cancellationToken) is null) return false;

        await transaction.ExecuteAsync("""
            delete from question_reports
            where question_id = @questionId
            """, new { questionId }, cancellationToken);
        return await content.SetStatusAsync(transaction, questionId, QuestionStatus.Approved, cancellationToken);
    }

    private sealed record ReportReasonRow(Guid QuestionId, long Count, string Reason);

    private sealed record ReportedQuestion(Guid QuestionId, long Count, IReadOnlyList<string> Reasons);
}
