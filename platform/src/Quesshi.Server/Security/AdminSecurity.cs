using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Quesshi.Application.Identity;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Identity;
using Quesshi.Infrastructure.Otp;
using Quesshi.Infrastructure.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Security;

/// <summary>A tenant-bound platform administration identity. It is intentionally separate from player identity.</summary>
public sealed record AdminIdentity(Guid TenantId, Guid AdminId, bool MustChangePassword)
{
    public string Subject => $"admin:{AdminId:N}";
}

internal interface IAdminSessionIssuer
{
    Task<bool> IssueAsync(HttpContext context, Guid tenantId, Guid adminId, bool mustChangePassword, CancellationToken cancellationToken = default);
}

internal interface IAdminSessionResolver
{
    Task<AdminIdentity?> ResolveAsync(Guid tenantId, string token, CancellationToken cancellationToken = default);
    Task RevokeAsync(string token, CancellationToken cancellationToken = default);
}

internal sealed class AdminSessionService(DatabaseSources sources, IClock clock) : IAdminSessionIssuer, IAdminSessionResolver
{
    internal const string CookieName = "__Host-qa";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    public async Task<bool> IssueAsync(HttpContext context, Guid tenantId, Guid adminId, bool mustChangePassword, CancellationToken cancellationToken = default)
    {
        var token = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(token);
        await using var connection = new Npgsql.NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        var issued = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            with eligible as (
                select a.id from admins a where a.id = @adminId and a.is_active
                  and (a.is_platform or exists (
                    select 1 from admin_memberships m where m.admin_id = a.id and m.tenant_id = @tenantId
                  ))
            ), created as (
                insert into auth_sessions (token_hash, tenant_id, admin_id, expires_at)
                select @hash, @tenantId, id, now() + interval '8 hours' from eligible
                returning 1
            ) select exists(select 1 from created)
            """, new { hash, tenantId, adminId }, cancellationToken: cancellationToken));
        if (!issued) return false;
        WriteCookie(context, token);
        return true;
    }

    public async Task<AdminIdentity?> ResolveAsync(Guid tenantId, string token, CancellationToken cancellationToken = default)
    {
        if (!TryTokenHash(token, out var hash)) return null;
        await using var connection = new Npgsql.NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<AdminIdentity>(new CommandDefinition("""
            select s.tenant_id as TenantId, s.admin_id as AdminId, a.must_change_password as MustChangePassword
            from auth_sessions s join admins a on a.id = s.admin_id
            where s.token_hash = @hash and s.tenant_id = @tenantId and s.expires_at > now() and a.is_active
              and (a.is_platform or exists (
                select 1 from admin_memberships m where m.admin_id = a.id and m.tenant_id = @tenantId
              ))
            """, new { hash, tenantId }, cancellationToken: cancellationToken));
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!TryTokenHash(token, out var hash)) return;
        await using var connection = new Npgsql.NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("delete from auth_sessions where token_hash = @hash and admin_id is not null", new { hash }, cancellationToken: cancellationToken));
    }

    private static bool TryTokenHash(string token, out byte[] hash)
    {
        hash = [];
        try
        {
            var bytes = Convert.FromBase64String(token);
            if (bytes.Length != 32) return false;
            hash = SHA256.HashData(bytes);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private void WriteCookie(HttpContext context, byte[] token) => context.Response.Cookies.Append(CookieName, Convert.ToBase64String(token), new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = Lifetime,
        Expires = clock.Now.Add(Lifetime)
    });
}

internal sealed class AdminSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAdminSessionResolver sessions)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is not null && context.Request.Cookies.TryGetValue(AdminSessionService.CookieName, out var token))
        {
            var admin = await sessions.ResolveAsync(tenant.Id, token, context.RequestAborted);
            if (admin is not null) context.Items[typeof(AdminIdentity)] = admin;
        }
        await next(context);
    }
}

internal sealed class AdminAntiforgery(IDataProtectionProvider protection, IClock clock)
{
    private readonly IDataProtector _protector = protection.CreateProtector("quesshi.admin-antiforgery.v1");

    public string Issue(HttpContext context)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("Antiforgery requires tenant resolution.");
        var payload = new Payload(tenant.Id, context.Admin()?.Subject ?? "anonymous", clock.Now, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
        return _protector.Protect(JsonSerializer.Serialize(payload));
    }

    public bool IsValid(HttpContext context, string? token)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null || string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token));
            return payload is not null && payload.TenantId == tenant.Id && payload.Subject == (context.Admin()?.Subject ?? "anonymous")
                && payload.IssuedAt >= clock.Now.AddDays(-1);
        }
        catch (CryptographicException) { return false; }
        catch (JsonException) { return false; }
    }

    private sealed record Payload(Guid TenantId, string Subject, DateTimeOffset IssuedAt, string Nonce);
}

public static class AdminServices
{
    public static IServiceCollection AddAdminServices(this IServiceCollection services)
    {
        services.AddSingleton<IAdminPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IAdminPasswordResetMailer, SmtpAdminMailer>();
        services.AddSingleton<AdminAuthService>();
        services.AddSingleton<AdminSessionService>();
        services.AddSingleton<IAdminSessionIssuer>(provider => provider.GetRequiredService<AdminSessionService>());
        services.AddSingleton<IAdminSessionResolver>(provider => provider.GetRequiredService<AdminSessionService>());
        services.AddSingleton<AdminAntiforgery>();
        return services;
    }

    public static IApplicationBuilder UseAdminSessionAuthentication(this IApplicationBuilder app) => app.UseMiddleware<AdminSessionMiddleware>();

    internal static AdminIdentity? Admin(this HttpContext context) => context.Items.TryGetValue(typeof(AdminIdentity), out var value) ? value as AdminIdentity : null;
}
