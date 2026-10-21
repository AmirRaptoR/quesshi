using System.Security.Cryptography;
using Npgsql;

namespace Quesshi.Server.Tests.TestSeeds;

internal sealed record SeedTenant(Guid Id, string Slug);

internal sealed record SeedCategory(Guid Id);

internal sealed record SeedQuestion(Guid Id);

internal sealed record SeedMedia(Guid Id);

internal sealed record SeedTenantContent(SeedTenant Tenant, SeedCategory Category, SeedQuestion Question);

internal sealed record SeedFinishedSession(Guid Id, Guid GuestId);

internal static class TenantSeed
{
    public static async Task<SeedTenant> CreateAsync(NpgsqlConnection connection, string name, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var slug = $"{name}-{id:N}";
        await SeedSql.ExecuteAsync(connection, """
            insert into tenants (id, slug, name, default_language)
            values (@id, @slug, @name, 'en');
            insert into tenant_languages (tenant_id, code, name, english_name)
            values (@id, 'en', 'English', 'English');
            """, cancellationToken, ("id", id), ("slug", slug), ("name", name));
        return new SeedTenant(id, slug);
    }
}

internal static class CategorySeed
{
    public static async Task<SeedCategory> CreateAsync(NpgsqlConnection connection, SeedTenant tenant, string name, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await SeedSql.ExecuteAsync(connection, """
            insert into categories (tenant_id, id, slug, names, is_active)
            values (@tenant, @id, @slug, '{"en":"Seed category"}'::jsonb, true)
            """, cancellationToken, ("tenant", tenant.Id), ("id", id), ("slug", $"{name}-{id:N}"));
        return new SeedCategory(id);
    }
}

internal static class QuestionSeed
{
    public static async Task<SeedQuestion> CreateAsync(NpgsqlConnection connection, SeedTenant tenant, SeedCategory category, string status, CancellationToken cancellationToken = default, Guid? id = null)
    {
        var questionId = id ?? Guid.NewGuid();
        var prompt = $"seed-question-{questionId:N}";
        await SeedSql.ExecuteAsync(connection, """
            insert into questions (tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source)
            values (@tenant, @id, 'choice', 1, 'trivia', 'en', @category, 1, @prompt,
                    '{"options":["one","two","three","four"],"correctIndex":0}'::jsonb,
                    @prompt, @status, 'admin')
            """, cancellationToken, ("tenant", tenant.Id), ("id", questionId), ("category", category.Id), ("prompt", prompt), ("status", status));
        return new SeedQuestion(questionId);
    }
}

internal static class SessionSeed
{
    public static async Task<SeedFinishedSession> CreateFinishedGuestSeatAsync(NpgsqlConnection connection, SeedTenant tenant, DateTimeOffset guestCreatedAt, DateTimeOffset finishedAt, CancellationToken cancellationToken = default)
    {
        var guestId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedSql.ExecuteAsync(connection, """
            insert into players (tenant_id, id, display_name, avatar_seed, lang, created_at)
            values (@tenant, @guest, 'Finished guest', 'finished-guest', 'en', @guestCreatedAt);
            insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, phase, started_at, ended_at)
            values (@tenant, @session, @code, 'test', @guest, 'en', 'finished', 1, '{}'::jsonb, 'finished', @finishedAt, @finishedAt);
            insert into session_participants (tenant_id, session_id, player_id, seat)
            values (@tenant, @session, @guest, 0)
            """, cancellationToken,
            ("tenant", tenant.Id), ("guest", guestId), ("guestCreatedAt", guestCreatedAt),
            ("session", sessionId), ("code", $"finished-{sessionId:N}"), ("finishedAt", finishedAt));
        return new SeedFinishedSession(sessionId, guestId);
    }
}

internal static class MediaSeed
{
    public static async Task<SeedMedia> CreateAsync(NpgsqlConnection connection, SeedTenant tenant, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var storageKey = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        await SeedSql.ExecuteAsync(connection, """
            insert into media_assets (tenant_id, id, kind, storage_key, mime, bytes)
            values (@tenant, @id, 'image', @storageKey, 'image/png', @bytes)
            """, cancellationToken, ("tenant", tenant.Id), ("id", id), ("storageKey", storageKey), ("bytes", bytes.Length));
        return new SeedMedia(id);
    }
}

/// <summary>One-call negative controls for PostgreSQL tests that must distinguish eligible data from excluded data.</summary>
internal static class DiscriminatingSeeds
{
    public static async Task<SeedTenantContent> AddForeignTenantContentAsync(NpgsqlConnection connection, string name, CancellationToken cancellationToken = default)
    {
        var tenant = await TenantSeed.CreateAsync(connection, name, cancellationToken);
        var category = await CategorySeed.CreateAsync(connection, tenant, "foreign", cancellationToken);
        var question = await QuestionSeed.CreateAsync(connection, tenant, category, "approved", cancellationToken);
        return new SeedTenantContent(tenant, category, question);
    }

    public static async Task<SeedTenantContent> AddUnselectedCategoryWithEligibleQuestionAsync(NpgsqlConnection connection, SeedTenant tenant, CancellationToken cancellationToken = default)
    {
        var category = await CategorySeed.CreateAsync(connection, tenant, "unselected", cancellationToken);
        var question = await QuestionSeed.CreateAsync(connection, tenant, category, "approved", cancellationToken);
        return new SeedTenantContent(tenant, category, question);
    }

    public static Task<SeedQuestion> AddIneligibleQuestionAsync(NpgsqlConnection connection, SeedTenant tenant, SeedCategory category, CancellationToken cancellationToken = default) =>
        QuestionSeed.CreateAsync(connection, tenant, category, "pending", cancellationToken);

    public static async Task<SeedQuestion> AddIneligibleQuestionBeforeAsync(NpgsqlConnection connection, SeedTenant tenant, SeedCategory category, Guid approvedQuestionId, long drawSeed, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Guid.NewGuid();
            if (!await SeedSql.OrdersBeforeAsync(connection, id, approvedQuestionId, drawSeed, cancellationToken)) continue;
            return await QuestionSeed.CreateAsync(connection, tenant, category, "pending", cancellationToken, id);
        }
    }

    public static Task<SeedFinishedSession> AddFinishedSessionAsync(NpgsqlConnection connection, SeedTenant tenant, DateTimeOffset guestCreatedAt, DateTimeOffset finishedAt, CancellationToken cancellationToken = default) =>
        SessionSeed.CreateFinishedGuestSeatAsync(connection, tenant, guestCreatedAt, finishedAt, cancellationToken);

    public static Task<SeedMedia> AddForeignMediaAsync(NpgsqlConnection connection, SeedTenant tenant, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        MediaSeed.CreateAsync(connection, tenant, bytes, cancellationToken);
}

internal static class SeedSql
{
    public static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<bool> OrdersBeforeAsync(NpgsqlConnection connection, Guid candidate, Guid existing, long seed, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select (hashtextextended(@candidate::text, @seed), @candidate::uuid)
                 < (hashtextextended(@existing::text, @seed), @existing::uuid)
            """, connection);
        command.Parameters.AddWithValue("candidate", candidate);
        command.Parameters.AddWithValue("existing", existing);
        command.Parameters.AddWithValue("seed", seed);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("The seed ordering query returned no value."));
    }
}
