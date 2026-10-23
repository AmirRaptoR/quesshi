using Quesshi.Plugin.Api;

namespace Quesshi.Features.Stats;

public sealed class StatsFeature : IFeature
{
    public string Id => "stats";

    public FeatureSettings Settings => FeatureSettings.Create(new StatsSettings(), static _ => []);

    public void Configure(IFeatureBuilder feature)
    {
        feature.On<SessionEnded>(ProjectSessionEndedAsync);
        feature.CleanupPlayer(CleanupPlayerAsync);
    }

    private static async Task ProjectSessionEndedAsync(ITenantTx transaction, SessionEnded @event, CancellationToken cancellationToken)
    {
        var isScored = await transaction.QuerySingleOrDefaultAsync<bool?>("""
            select exists(
                select 1
                from sessions s
                where s.id = @sessionId
                  and s.status = 'finished'
                  and s.end_reason in ('completed', 'forfeit')
                  and s.scored) as is_scored
            """, new { sessionId = @event.SessionId }, cancellationToken);
        if (isScored != true) return;

        var results = await transaction.QueryAsync<ResultRow>("""
            select p.player_id as PlayerId,
                   s.format as Format,
                   s.ended_at as EndedAt,
                   coalesce(p.outcome, 'none') as Outcome,
                   p.score as Points
            from sessions s
            join session_participants p on p.session_id = s.id
            join players player on player.id = p.player_id
            where s.id = @sessionId
              and p.role = 'player'
              and player.deleted_at is null
              and coalesce((p.result ->> 'runBegan')::boolean, false)
            order by p.player_id
            """, new { sessionId = @event.SessionId }, cancellationToken);

        foreach (var result in results)
        {
            var inserted = await transaction.ExecuteAsync("""
                insert into player_results (tenant_id, player_id, session_id, format, ended_at, outcome, points)
                values (app_tenant(), @playerId, @sessionId, @format, @endedAt, @outcome, @points)
                on conflict do nothing
                """, new
            {
                result.PlayerId,
                SessionId = @event.SessionId,
                result.Format,
                result.EndedAt,
                result.Outcome,
                result.Points
            }, cancellationToken);
            if (inserted == 0) continue;

            await transaction.ExecuteAsync("""
                insert into player_stats (tenant_id, player_id, format, played, wins, losses, draws, points)
                values (app_tenant(), @playerId, @format, 1,
                        case when @outcome = 'win' then 1 else 0 end,
                        case when @outcome = 'loss' then 1 else 0 end,
                        case when @outcome = 'draw' then 1 else 0 end,
                        @points)
                on conflict (tenant_id, player_id, format) do update set
                    played = player_stats.played + 1,
                    wins = player_stats.wins + excluded.wins,
                    losses = player_stats.losses + excluded.losses,
                    draws = player_stats.draws + excluded.draws,
                    points = player_stats.points + excluded.points
                """, result, cancellationToken);
            await UpdateStreakAsync(transaction, result.PlayerId, result.Format, cancellationToken);
        }

        await ProjectCategoryAccuracyAsync(transaction, @event.SessionId, cancellationToken);
        await ProjectQuestionAccuracyAsync(transaction, @event.SessionId, cancellationToken);
        await ProjectQuestionServedAsync(transaction, @event.SessionId, cancellationToken);
    }

    private static async Task ProjectCategoryAccuracyAsync(ITenantTx transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        var rows = await transaction.QueryAsync<CategoryAccuracyRow>("""
            select a.player_id as PlayerId,
                   i.category_id as CategoryId,
                   count(*)::int as Asked,
                   coalesce(sum(a.credit), 0)::double precision as CreditSum
            from session_answers a
            join session_items i on i.session_id = a.session_id and i.slot = a.slot
            join session_participants p on p.session_id = a.session_id and p.player_id = a.player_id
            join players player on player.id = a.player_id
            where a.session_id = @sessionId
              and a.credit is not null
              and i.category_id is not null
              and p.role = 'player'
              and player.deleted_at is null
            group by a.player_id, i.category_id
            order by a.player_id, i.category_id
            """, new { sessionId }, cancellationToken);
        foreach (var row in rows)
        {
            await transaction.ExecuteAsync("""
                insert into player_category_stats (tenant_id, player_id, category_id, asked, credit_sum)
                values (app_tenant(), @playerId, @categoryId, @asked, @creditSum)
                on conflict (tenant_id, player_id, category_id) do update set
                    asked = player_category_stats.asked + excluded.asked,
                    credit_sum = player_category_stats.credit_sum + excluded.credit_sum
                """, row, cancellationToken);
        }
    }

    private static async Task ProjectQuestionAccuracyAsync(ITenantTx transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        var rows = await transaction.QueryAsync<QuestionAccuracyRow>("""
            select i.question_id as QuestionId,
                   count(*) as Asked,
                   coalesce(sum(a.credit), 0)::double precision as CreditSum
            from session_answers a
            join session_items i on i.session_id = a.session_id and i.slot = a.slot
            join session_participants p on p.session_id = a.session_id and p.player_id = a.player_id
            join players player on player.id = a.player_id
            where a.session_id = @sessionId
              and a.credit is not null
              and i.question_id is not null
              and p.role = 'player'
              and player.deleted_at is null
            group by i.question_id
            order by i.question_id
            """, new { sessionId }, cancellationToken);
        foreach (var row in rows)
        {
            await transaction.ExecuteAsync("""
                insert into question_stats (tenant_id, question_id, asked, credit_sum)
                values (app_tenant(), @questionId, @asked, @creditSum)
                on conflict (tenant_id, question_id) do update set
                    asked = question_stats.asked + excluded.asked,
                    credit_sum = question_stats.credit_sum + excluded.credit_sum
                """, row, cancellationToken);
        }
    }

    private static async Task ProjectQuestionServedAsync(ITenantTx transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        var rows = await transaction.QueryAsync<QuestionServedRow>("""
            select i.question_id as QuestionId, count(*) as Served
            from sessions s
            join session_items i on i.session_id = s.id
            where s.id = @sessionId
              and i.question_id is not null
              and (
                (s.slot >= 0 and i.slot <= s.slot)
                or (s.slot < 0 and exists (
                    select 1
                    from session_participants p
                    where p.session_id = s.id
                      and p.role = 'player'
                      and p.slot >= i.slot
                ))
            )
            group by i.question_id
            order by i.question_id
            """, new { sessionId }, cancellationToken);
        foreach (var row in rows)
        {
            await transaction.ExecuteAsync("""
                insert into question_stats (tenant_id, question_id, served)
                values (app_tenant(), @questionId, @served)
                on conflict (tenant_id, question_id) do update set
                    served = question_stats.served + excluded.served
                """, row, cancellationToken);
        }
    }

    private static Task CleanupPlayerAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("""
            delete from player_results where player_id = @playerId;
            delete from player_stats where player_id = @playerId;
            delete from player_category_stats where player_id = @playerId;
            """, new { playerId }, cancellationToken);

    private static async Task UpdateStreakAsync(ITenantTx transaction, Guid playerId, string format, CancellationToken cancellationToken)
    {
        var streak = await transaction.QuerySingleOrDefaultAsync<StreakRow>("""
            with ordered as (
                select outcome,
                       count(*) filter (where outcome = 'loss') over (order by ended_at, session_id) as run
                from player_results
                where player_id = @playerId and format = @format
            ), runs as (
                select run, count(*) filter (where outcome = 'win')::int as wins
                from ordered
                group by run
            )
            select coalesce((array_agg(wins order by run desc))[1], 0)::int as Current,
                   coalesce(max(wins), 0)::int as Best
            from runs
            """, new { playerId, format }, cancellationToken) ?? new StreakRow(0, 0);
        await transaction.ExecuteAsync("""
            update player_stats
            set streak = @current, best_streak = @best
            where player_id = @playerId and format = @format
            """, new { playerId, format, streak.Current, streak.Best }, cancellationToken);
    }

    private sealed record StatsSettings;

    private sealed record ResultRow(Guid PlayerId, string Format, DateTimeOffset EndedAt, string Outcome, int Points);

    private sealed record CategoryAccuracyRow(Guid PlayerId, Guid CategoryId, int Asked, double CreditSum);

    private sealed record QuestionAccuracyRow(Guid QuestionId, long Asked, double CreditSum);

    private sealed record QuestionServedRow(Guid QuestionId, long Served);

    private sealed record StreakRow(int Current, int Best);
}
