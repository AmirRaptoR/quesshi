using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Stats;

internal static partial class StatsReadRoutes
{
    public const string ScoreQuery = "leaderboard.score";

    public static void MapPlayerRoutes(RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/me", GetOwnSummaryAsync);
        routes.MapGet("/players/{playerId:guid}", GetPlayerSummaryAsync);
    }

    public static void MapAdminRoutes(RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/questions", (
            string? ids,
            HttpContext context,
            ITenantRequestContext tenants,
            ITenantDb database,
            CancellationToken cancellationToken) => GetQuestionUsageAsync(ids, context, tenants, database, cancellationToken));
    }

    public static async Task<StatsPlayerSummary?> GetSummaryAsync(
        ITenantTx transaction,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var playerExists = await transaction.QuerySingleOrDefaultAsync<bool?>("""
            select true
            from players
            where id = @playerId and deleted_at is null
            """, new { playerId }, cancellationToken);
        if (playerExists is not true) return null;

        var formats = await transaction.QueryAsync<FormatRow>("""
            select format as Format,
                   played as Played,
                   wins as Wins,
                   losses as Losses,
                   draws as Draws,
                   points as Points,
                   streak as Streak,
                   best_streak as BestStreak
            from player_stats
            where player_id = @playerId
            order by format
            """, new { playerId }, cancellationToken);
        var categories = await transaction.QueryAsync<CategoryRow>("""
            select stats.category_id as CategoryId,
                   stats.asked as Asked,
                   stats.credit_sum as CreditSum,
                   category.names::text as NamesJson
            from player_category_stats stats
            join categories category on category.id = stats.category_id
            where stats.player_id = @playerId
            order by stats.category_id
            """, new { playerId }, cancellationToken);

        var formatSummaries = formats.Select(static row => new StatsFormatSummary(
            row.Format, row.Played, row.Wins, row.Losses, row.Draws, row.Points, row.Streak, row.BestStreak)).ToArray();
        return new StatsPlayerSummary(
            playerId,
            formatSummaries,
            categories.Select(static row => new StatsCategorySummary(
                row.CategoryId,
                row.Asked,
                row.CreditSum,
                JsonSerializer.Deserialize(row.NamesJson, StatsJsonContext.Default.DictionaryStringString) ?? [])).ToArray(),
            formatSummaries.Sum(static format => format.Points));
    }

    /// <summary>The summary with its total taken from the all-time board when <c>leaderboard.score</c> is offered.</summary>
    public static async Task<StatsPlayerSummary?> GetSummaryWithBoardTotalAsync(
        ITenantTx transaction,
        INamedQueries queries,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var summary = await GetSummaryAsync(transaction, playerId, cancellationToken);
        if (summary is null) return null;

        var score = await queries.InvokeAsync<long>("stats", ScoreQuery, transaction, playerId, cancellationToken);
        return score.Available ? summary with { Total = score.Value } : summary;
    }

    private static async Task<IResult> GetOwnSummaryAsync(
        HttpContext context,
        ITenantRequestContext tenants,
        ITenantDb database,
        INamedQueries queries,
        CancellationToken cancellationToken)
    {
        if (!TryGetPlayer(context.User, out var playerId) || tenants.GetTenantId(context) is not { } tenantId)
        {
            return Results.Unauthorized();
        }

        return await GetSummaryResponseAsync(database, queries, tenantId, playerId, cancellationToken);
    }

    private static async Task<IResult> GetPlayerSummaryAsync(
        Guid playerId,
        HttpContext context,
        ITenantRequestContext tenants,
        ITenantDb database,
        INamedQueries queries,
        CancellationToken cancellationToken)
    {
        if (tenants.GetTenantId(context) is not { } tenantId) return Results.NotFound();
        return await GetSummaryResponseAsync(database, queries, tenantId, playerId, cancellationToken);
    }

    private static async Task<IResult> GetSummaryResponseAsync(
        ITenantDb database,
        INamedQueries queries,
        Guid tenantId,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var summary = await database.InTenantAsync(
            tenantId,
            transaction => GetSummaryWithBoardTotalAsync(transaction, queries, playerId, cancellationToken),
            cancellationToken: cancellationToken,
            readOnly: true);
        return summary is null ? Results.NotFound() : Results.Ok(summary);
    }

    private static async Task<IResult> GetQuestionUsageAsync(
        string? ids,
        HttpContext context,
        ITenantRequestContext tenants,
        ITenantDb database,
        CancellationToken cancellationToken)
    {
        if (!TryParseQuestionIds(ids, out var questionIds))
        {
            return Results.BadRequest(new { error = "invalid_question_ids" });
        }
        if (tenants.GetTenantId(context) is not { } tenantId) return Results.NotFound();

        var usage = await database.InTenantAsync(
            tenantId,
            transaction => GetQuestionUsageAsync(transaction, questionIds, cancellationToken),
            cancellationToken: cancellationToken,
            readOnly: true);
        return Results.Ok(usage);
    }

    private static async Task<IReadOnlyList<StatsQuestionUsage>> GetQuestionUsageAsync(
        ITenantTx transaction,
        IReadOnlyList<Guid> questionIds,
        CancellationToken cancellationToken)
    {
        var rows = await transaction.QueryAsync<QuestionUsageRow>("""
            select question.id as QuestionId,
                   coalesce(stats.served, 0) as Served,
                   coalesce(stats.asked, 0) as Asked,
                   case when coalesce(stats.asked, 0) = 0 then 0::double precision
                        else stats.credit_sum / stats.asked end as Accuracy
            from questions question
            left join question_stats stats on stats.question_id = question.id
            where question.id = any(@questionIds)
            order by array_position(@questionIds, question.id)
            """, new { questionIds = questionIds.ToArray() }, cancellationToken);
        return rows.Select(static row => new StatsQuestionUsage(row.QuestionId, row.Served, row.Asked, row.Accuracy)).ToArray();
    }

    private static bool TryGetPlayer(ClaimsPrincipal principal, out Guid playerId) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out playerId);

    private static bool TryParseQuestionIds(string? value, out IReadOnlyList<Guid> questionIds)
    {
        questionIds = [];
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parsed = new List<Guid>();
        foreach (var segment in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(segment, out var id)) return false;
            if (!parsed.Contains(id)) parsed.Add(id);
        }

        questionIds = parsed;
        return questionIds.Count > 0;
    }

    private sealed record FormatRow(string Format, int Played, int Wins, int Losses, int Draws, long Points, int Streak, int BestStreak);

    private sealed record CategoryRow(Guid CategoryId, int Asked, double CreditSum, string NamesJson);

    private sealed record QuestionUsageRow(Guid QuestionId, long Served, long Asked, double Accuracy);

    [JsonSerializable(typeof(Dictionary<string, string>))]
    private sealed partial class StatsJsonContext : JsonSerializerContext;
}
