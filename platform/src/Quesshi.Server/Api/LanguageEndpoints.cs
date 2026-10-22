using Microsoft.AspNetCore.Mvc;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Tenant language administration routes.</summary>
public sealed class LanguageEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        var languages = routes.AdminRoutes(AdminRole.Owner).MapGroup("/languages");
        languages.MapGet("", ListAsync);
        languages.MapPost("", CreateAsync).WithMetadata(new AdminAuditMetadata("admin.language.create", "language"));
        languages.MapPut("/default", SetDefaultAsync).WithMetadata(new AdminAuditMetadata("admin.language.default.update", "language"));
        languages.MapPut("/{code}", UpdateAsync).WithMetadata(new AdminAuditMetadata("admin.language.update", "language"));
        languages.MapDelete("/{code}", DeleteAsync).WithMetadata(new AdminAuditMetadata("admin.language.delete", "language"));
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        [FromServices] ITenantDb database,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();

        var languages = await database.InTenantAsync<IReadOnlyList<LanguageResponse>>(tenant.Id, async transaction =>
        {
            var defaultLanguage = await transaction.QuerySingleOrDefaultAsync<string>(
                "select default_language from tenants",
                cancellationToken: cancellationToken);
            var rows = await transaction.QueryAsync<LanguageRow>("""
                select code as Code, name as Name, english_name as EnglishName, direction as Direction
                from tenant_languages
                order by sort_order, code
                """, cancellationToken: cancellationToken);
            return rows.Select(row => ToResponse(row, defaultLanguage)).ToArray();
        }, cancellationToken: cancellationToken, readOnly: true);
        return Results.Ok(languages);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        LanguageCreateRequest request,
        [FromServices] ITenantDb database,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (!TryNormalize(request.Code, request.Name, request.EnglishName, request.Direction, out var language))
            return Error("invalid_language", StatusCodes.Status400BadRequest);

        var result = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var defaultLanguage = await LockDefaultLanguageAsync(transaction, cancellationToken);
            if (defaultLanguage is null) return LanguageChangeResult.Missing;

            var created = await transaction.QuerySingleOrDefaultAsync<LanguageRow>("""
                insert into tenant_languages (tenant_id, code, name, english_name, direction)
                values (@tenantId, @code, @name, @englishName, @direction)
                on conflict (tenant_id, code) do nothing
                returning code as Code, name as Name, english_name as EnglishName, direction as Direction
                """, new
            {
                tenantId = tenant.Id,
                language.Code,
                language.Name,
                language.EnglishName,
                language.Direction,
            }, cancellationToken);
            if (created is null) return LanguageChangeResult.Duplicate;
            if (!await NotifyConfigurationChangedAsync(transaction, tenant.Id, cancellationToken)) return LanguageChangeResult.Missing;
            return new LanguageChangeResult(LanguageChangeStatus.Success, ToResponse(created, defaultLanguage));
        }, cancellationToken: cancellationToken);

        return Answer(result, created: true);
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context,
        string code,
        LanguageUpdateRequest request,
        [FromServices] ITenantDb database,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (!TryNormalize(code, request.Name, request.EnglishName, request.Direction, out var language))
            return Error("invalid_language", StatusCodes.Status400BadRequest);

        var result = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var defaultLanguage = await LockDefaultLanguageAsync(transaction, cancellationToken);
            if (defaultLanguage is null) return LanguageChangeResult.Missing;

            var updated = await transaction.QuerySingleOrDefaultAsync<LanguageRow>("""
                update tenant_languages
                set name = @name, english_name = @englishName, direction = @direction
                where code = @code
                returning code as Code, name as Name, english_name as EnglishName, direction as Direction
                """, new
            {
                language.Code,
                language.Name,
                language.EnglishName,
                language.Direction,
            }, cancellationToken);
            if (updated is null) return LanguageChangeResult.Missing;
            if (!await NotifyConfigurationChangedAsync(transaction, tenant.Id, cancellationToken)) return LanguageChangeResult.Missing;
            return LanguageChangeResult.Success;
        }, cancellationToken: cancellationToken);

        return Answer(result);
    }

    private static async Task<IResult> SetDefaultAsync(
        HttpContext context,
        LanguageDefaultRequest request,
        [FromServices] ITenantDb database,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code)) return Error("invalid_language", StatusCodes.Status400BadRequest);

        var result = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var current = await LockDefaultLanguageAsync(transaction, cancellationToken);
            if (current is null) return LanguageChangeResult.Missing;
            if (string.Equals(current, code, StringComparison.Ordinal)) return LanguageChangeResult.Success;

            var offered = await transaction.QuerySingleOrDefaultAsync<string>(
                "select code from tenant_languages where code = @code",
                new { code },
                cancellationToken);
            if (offered is null) return LanguageChangeResult.Missing;

            var version = await transaction.QuerySingleOrDefaultAsync<long?>("""
                update tenants
                set default_language = @code, config_version = config_version + 1
                where id = @tenantId
                returning config_version
                """, new { tenantId = tenant.Id, code }, cancellationToken);
            if (version is null) return LanguageChangeResult.Missing;
            await transaction.ExecuteAsync(
                "select pg_notify('quesshi_config', @message)",
                new { message = $"config:{tenant.Id}:{version.Value}" },
                cancellationToken);
            return LanguageChangeResult.Success;
        }, cancellationToken: cancellationToken);

        return Answer(result);
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        string code,
        [FromServices] ITenantDb database,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        code = code.Trim();
        if (string.IsNullOrEmpty(code)) return Error("invalid_language", StatusCodes.Status400BadRequest);

        var result = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var defaultLanguage = await LockDefaultLanguageAsync(transaction, cancellationToken);
            if (defaultLanguage is null) return LanguageChangeResult.Missing;
            if (string.Equals(defaultLanguage, code, StringComparison.Ordinal)) return LanguageChangeResult.DefaultLanguage;

            var deleted = await transaction.ExecuteAsync(
                "delete from tenant_languages where code = @code",
                new { code },
                cancellationToken);
            if (deleted != 1) return LanguageChangeResult.Missing;
            return await NotifyConfigurationChangedAsync(transaction, tenant.Id, cancellationToken)
                ? LanguageChangeResult.Success
                : LanguageChangeResult.Missing;
        }, cancellationToken: cancellationToken);

        return Answer(result);
    }

    private static async Task<string?> LockDefaultLanguageAsync(ITenantTx transaction, CancellationToken cancellationToken) =>
        await transaction.QuerySingleOrDefaultAsync<string>("""
            select default_language
            from tenants
            for update
            """, cancellationToken: cancellationToken);

    private static async Task<bool> NotifyConfigurationChangedAsync(
        ITenantTx transaction,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var version = await transaction.QuerySingleOrDefaultAsync<long?>("""
            update tenants
            set config_version = config_version + 1
            where id = @tenantId
            returning config_version
            """, new { tenantId }, cancellationToken);
        if (version is null) return false;
        await transaction.ExecuteAsync(
            "select pg_notify('quesshi_config', @message)",
            new { message = $"config:{tenantId}:{version.Value}" },
            cancellationToken);
        return true;
    }

    private static bool TryNormalize(
        string? code,
        string? name,
        string? englishName,
        string? direction,
        out LanguageWrite language)
    {
        code = code?.Trim();
        name = name?.Trim();
        englishName = englishName?.Trim();
        direction = direction?.Trim();
        if (string.IsNullOrEmpty(code) ||
            string.IsNullOrEmpty(name) ||
            string.IsNullOrEmpty(englishName) ||
            !string.Equals(direction, "ltr", StringComparison.Ordinal) &&
            !string.Equals(direction, "rtl", StringComparison.Ordinal))
        {
            language = null!;
            return false;
        }

        language = new LanguageWrite(code!, name!, englishName!, direction!);
        return true;
    }

    private static LanguageResponse ToResponse(LanguageRow row, string? defaultLanguage) =>
        new(row.Code, row.Name, row.EnglishName, row.Direction, string.Equals(row.Code, defaultLanguage, StringComparison.Ordinal));

    private static IResult Answer(LanguageChangeResult result, bool created = false) => result.Status switch
    {
        LanguageChangeStatus.Success when created => Results.Created(
            $"/api/admin/languages/{Uri.EscapeDataString(result.Language!.Code)}",
            result.Language),
        LanguageChangeStatus.Success when result.Language is not null => Results.Ok(result.Language),
        LanguageChangeStatus.Success => Results.NoContent(),
        LanguageChangeStatus.Duplicate => Error("language_taken", StatusCodes.Status409Conflict),
        LanguageChangeStatus.DefaultLanguage => Error("default_language_cannot_be_removed", StatusCodes.Status409Conflict),
        LanguageChangeStatus.Missing => Results.NotFound(),
        _ => Error("language_change_failed", StatusCodes.Status400BadRequest),
    };

    private static IResult Error(string error, int statusCode) => Results.Json(new { error }, statusCode: statusCode);

    private sealed record LanguageCreateRequest(string? Code, string? Name, string? EnglishName, string? Direction);

    private sealed record LanguageUpdateRequest(string? Name, string? EnglishName, string? Direction);

    private sealed record LanguageDefaultRequest(string? Code);

    private sealed record LanguageWrite(string Code, string Name, string EnglishName, string Direction);

    private sealed record LanguageRow(string Code, string Name, string EnglishName, string Direction);

    private sealed record LanguageResponse(string Code, string Name, string EnglishName, string Direction, bool IsDefault);

    private sealed record LanguageChangeResult(LanguageChangeStatus Status, LanguageResponse? Language = null)
    {
        public static LanguageChangeResult Success { get; } = new(LanguageChangeStatus.Success);

        public static LanguageChangeResult Duplicate { get; } = new(LanguageChangeStatus.Duplicate);

        public static LanguageChangeResult Missing { get; } = new(LanguageChangeStatus.Missing);

        public static LanguageChangeResult DefaultLanguage { get; } = new(LanguageChangeStatus.DefaultLanguage);
    }

    private enum LanguageChangeStatus
    {
        Success,
        Duplicate,
        Missing,
        DefaultLanguage,
    }
}
