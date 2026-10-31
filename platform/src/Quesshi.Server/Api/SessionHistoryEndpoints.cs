using Quesshi.Application.Sessions;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Tenant-scoped, paged reads of the signed-in player's session history and a finished session's questions.</summary>
public sealed class SessionHistoryEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        routes.PlayerRoutes.MapGet("/me/sessions", SessionsAsync).AllowGuests();
        routes.PlayerRoutes.MapGet("/sessions/{id:guid}/history", QuestionsAsync).AllowGuests();
    }

    private static async Task<IResult> SessionsAsync(
        HttpContext context,
        ITenantDb database,
        string? before,
        int? limit,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("History access requires tenant resolution.");
        var player = context.Player() ?? throw new InvalidOperationException("History access requires player authentication.");
        var page = await new SessionHistoryReader(database).ReadSessionsAsync(tenant.Id, player.PlayerId, before, limit, cancellationToken);
        return page is null ? Error("invalid_cursor", 400) : Results.Ok(page);
    }

    private static async Task<IResult> QuestionsAsync(
        Guid id,
        HttpContext context,
        ITenantDb database,
        int? after,
        int? limit,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("History access requires tenant resolution.");
        var player = context.Player() ?? throw new InvalidOperationException("History access requires player authentication.");
        if (after is < -1) return Error("invalid_cursor", 400);
        var result = await new SessionHistoryReader(database).ReadQuestionsAsync(tenant.Id, player.PlayerId, id, after, limit, cancellationToken);
        return result.Status switch
        {
            SessionQuestionHistoryStatus.Found => Results.Ok(result.Page),
            SessionQuestionHistoryStatus.NotFinished => Error("not_finished", 409),
            _ => Error("not_found", 404),
        };
    }

    private static IResult Error(string error, int status) => Results.Json(new { error }, statusCode: status);
}
