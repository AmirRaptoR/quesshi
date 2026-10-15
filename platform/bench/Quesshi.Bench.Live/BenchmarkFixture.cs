using Dapper;
using Npgsql;

namespace Quesshi.Bench.Live;

/// <summary>Seeds the same production tables that the live runner loads through the snapshot path.</summary>
public static class BenchmarkFixture
{
    public static async Task<BenchmarkTenant> SeedTenantAsync(string ownerConnectionString, string tenantHost, CancellationToken cancellationToken = default)
    {
        var tenantId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            insert into tenants (id, slug, name, default_language)
            values (@Id, @Slug, 'Benchmark tenant', 'en');
            insert into tenant_hosts (tenant_id, host) values (@Id, @Host);
            insert into tenant_languages (tenant_id, code, name, english_name, direction, sort_order)
            values (@Id, 'en', 'English', 'English', 'ltr', 0);
            insert into tenant_capabilities (tenant_id, type, key, entitled, enabled)
            values (@Id, 'switch', 'guests', true, true);
            """, new { Id = tenantId, Slug = $"bench-{tenantId:N}", Host = tenantHost }, cancellationToken: cancellationToken));
        return new BenchmarkTenant(tenantId);
    }

    public static async Task<BenchmarkSession> SeedSessionAsync(
        string ownerConnectionString,
        BenchmarkTenant tenant,
        BenchmarkOptions options,
        IReadOnlyList<Guid> playerIds,
        CancellationToken cancellationToken = default)
    {
        var session = new BenchmarkSession(Guid.NewGuid(), playerIds[0]);
        var now = DateTimeOffset.UtcNow;
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string insertSession = """
            insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, rules, phase, slot, step, phase_started_at, due_at, started_at)
            values (@TenantId, @Id, @Code, 'bench_live', @OwnerId, 'en', 'running', @Capacity, '{}'::jsonb, '{}'::jsonb, '0', 0, 0, @Now, @DueAt, @Now);
            """;
        await connection.ExecuteAsync(new CommandDefinition(insertSession, new
        {
            TenantId = tenant.Id,
            session.Id,
            Code = $"B{session.Id.ToString("N")[..7].ToUpperInvariant()}",
            session.OwnerId,
            Capacity = options.Players,
            Now = now,
            DueAt = now + BenchLiveFormat.Countdown,
        }, transaction, cancellationToken: cancellationToken));

        const string insertParticipant = """
            insert into session_participants (tenant_id, session_id, player_id, seat, role, status, eligible, phase, slot)
            values (@TenantId, @SessionId, @PlayerId, @Seat, 'player', 'active', true, '0', 0);
            """;
        for (var seat = 0; seat < playerIds.Count; seat++)
        {
            await connection.ExecuteAsync(new CommandDefinition(insertParticipant, new { TenantId = tenant.Id, SessionId = session.Id, PlayerId = playerIds[seat], Seat = seat }, transaction, cancellationToken: cancellationToken));
        }

        const string insertItem = """
            insert into session_items (tenant_id, session_id, slot, kind, kind_version, prompt, payload, resolved, card, reveal)
            values (@TenantId, @SessionId, @Slot, 'choice', 1, @Prompt, @Payload::jsonb, '{}'::jsonb, @Card::jsonb, @Reveal::jsonb);
            """;
        for (var slot = 0; slot < options.ItemCount; slot++)
        {
            var item = BenchmarkItem.Create(slot);
            await connection.ExecuteAsync(new CommandDefinition(insertItem, new
            {
                TenantId = tenant.Id,
                SessionId = session.Id,
                Slot = slot,
                item.Prompt,
                Payload = item.Payload.GetRawText(),
                Card = item.Card.GetRawText(),
                Reveal = item.Reveal.GetRawText(),
            }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return session;
    }
}

public sealed record BenchmarkTenant(Guid Id);
public sealed record BenchmarkSession(Guid Id, Guid OwnerId);
