using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Leaderboard;

/// <summary>
/// Read side of the leaderboard (design 13). A rank is the count of strictly higher scores plus one, so tied
/// players share it. Everything is plain SQL inside the caller's tenant transaction.
/// </summary>
/// <summary>One ranked player. Serialises to the shape of <c>Quesshi.Shared.LeaderboardRowDto</c>, which the client reads.</summary>
public sealed record LeaderboardRow(int Rank, Guid PlayerId, string Name, string Avatar, long Score);

/// <summary>A board page, shaped like <c>Quesshi.Shared.LeaderboardDto</c>: at most 100 rows plus the caller's own.</summary>
public sealed record LeaderboardPage(IReadOnlyList<LeaderboardRow> Entries, LeaderboardRow? Me);

public static partial class LeaderboardReadRoutes
{
    public const int TopCount = 100;

    /// <summary>The feature's query declared in <c>Uses</c>: the caller's friend ids as <c>IReadOnlyList&lt;Guid&gt;</c>.</summary>
    public const string FriendsQuery = "friends.ids";

    /// <summary>The top entries of one board and period, served by the <c>leaderboard_top</c> index.</summary>
    public const string TopSql = """
        with top as (
            select s.player_id,
                   s.score,
                   rank() over (order by s.score desc) as rank
            from leaderboard_scores s
            where s.board = @board and s.period = @period
            order by s.score desc, s.player_id
            limit 100
        )
        select top.rank::int as Rank, p.id as PlayerId, p.display_name as Name, p.avatar_seed as Avatar, top.score as Score
        from top
        join players p on p.id = top.player_id and p.deleted_at is null
        order by top.score desc, top.player_id
        """;

    private const string MeSql = """
        select (select count(*) + 1 from leaderboard_scores o
                where o.board = @board and o.period = @period and o.score > s.score)::int as Rank,
               p.id as PlayerId, p.display_name as Name, p.avatar_seed as Avatar, s.score as Score
        from leaderboard_scores s
        join players p on p.id = s.player_id and p.deleted_at is null
        where s.board = @board and s.period = @period and s.player_id = @playerId
        """;

    private const string FriendsSql = """
        with scoped as (
            select p.id, p.display_name, p.avatar_seed, coalesce(s.score, 0) as score
            from players p
            left join leaderboard_scores s
              on s.player_id = p.id and s.board = @board and s.period = @period
            where p.id = any(@ids) and p.deleted_at is null
        )
        select rank() over (order by score desc)::int as Rank,
               id as PlayerId, display_name as Name, avatar_seed as Avatar, score as Score
        from scoped
        order by score desc, id
        """;

    public static void MapPlayerRoutes(RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/{board}", GetBoardAsync);
    }

    /// <summary>The named query <c>leaderboard.score</c>: a player's all-time score on board <c>all</c>, 0 without a row.</summary>
    public static async Task<long> GetScoreAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken) =>
        await transaction.QuerySingleOrDefaultAsync<long?>("""
            select score from leaderboard_scores
            where board = 'all' and period = 'all' and player_id = @playerId
            """, new { playerId }, cancellationToken) ?? 0;

    private static async Task<IResult> GetBoardAsync(
        string board,
        string? period,
        string? scope,
        HttpContext context,
        ITenantRequestContext tenants,
        ITenantDb database,
        INamedQueries queries,
        CancellationToken cancellationToken)
    {
        period ??= "all";
        scope ??= "all";
        if (!BoardPattern().IsMatch(board) || !IsPeriod(period) || scope is not ("all" or "friends"))
        {
            return Results.BadRequest(new { error = "invalid_request" });
        }
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var playerId)
            || tenants.GetTenantId(context) is not { } tenantId)
        {
            return Results.Unauthorized();
        }

        var view = await database.InTenantAsync(tenantId, async transaction =>
        {
            if (scope == "all") return await ReadAllAsync(transaction, board, period, playerId, cancellationToken);

            var friends = await queries.InvokeAsync<IReadOnlyList<Guid>>("leaderboard", FriendsQuery, transaction, playerId, cancellationToken);
            if (!friends.Available) return null;
            return await ReadFriendsAsync(transaction, board, period, playerId, friends.Value ?? [], cancellationToken);
        }, cancellationToken: cancellationToken, readOnly: true);

        return view is null
            ? Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(view);
    }

    private static async Task<LeaderboardPage> ReadAllAsync(
        ITenantTx transaction, string board, string period, Guid playerId, CancellationToken cancellationToken)
    {
        var entries = await transaction.QueryAsync<LeaderboardRow>(TopSql, new { board, period }, cancellationToken);
        var me = entries.FirstOrDefault(entry => entry.PlayerId == playerId)
            ?? await transaction.QuerySingleOrDefaultAsync<LeaderboardRow>(MeSql, new { board, period, playerId }, cancellationToken);
        return new LeaderboardPage(entries, me);
    }

    private static async Task<LeaderboardPage> ReadFriendsAsync(
        ITenantTx transaction, string board, string period, Guid playerId, IReadOnlyList<Guid> friendIds, CancellationToken cancellationToken)
    {
        var ids = friendIds.Append(playerId).Distinct().ToArray();
        var ranked = await transaction.QueryAsync<LeaderboardRow>(FriendsSql, new { board, period, ids }, cancellationToken);
        return new LeaderboardPage([.. ranked.Take(TopCount)], ranked.FirstOrDefault(entry => entry.PlayerId == playerId));
    }

    private static bool IsPeriod(string period) => period == "all" || MonthPattern().IsMatch(period) || WeekPattern().IsMatch(period);

    [GeneratedRegex("^[a-z0-9_]{1,64}$")]
    private static partial Regex BoardPattern();

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")]
    private static partial Regex MonthPattern();

    [GeneratedRegex(@"^\d{4}-W(0[1-9]|[1-4]\d|5[0-3])$")]
    private static partial Regex WeekPattern();
}
