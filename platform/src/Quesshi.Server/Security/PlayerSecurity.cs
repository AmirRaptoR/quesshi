using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Security;

/// <summary>The tenant-bound player identity carried by the player session cookie.</summary>
public sealed record PlayerIdentity(Guid TenantId, Guid PlayerId, Guid? AccountId, bool IsGuest)
{
    public string Subject => IsGuest
        ? $"guest:{PlayerId:N}"
        : $"account:{PlayerId:N}:{AccountId?.ToString("N") ?? "none"}";
}

/// <summary>Stable issuance surface for sign-in and guest-entry endpoint modules.</summary>
public interface IPlayerSessionIssuer
{
    Task IssueAccountAsync(HttpContext context, Guid tenantId, Guid accountId, CancellationToken cancellationToken = default);
    Task IssueGuestAsync(HttpContext context, Guid tenantId, Guid playerId, CancellationToken cancellationToken = default);
}

internal interface IPlayerSessionResolver
{
    Task<PlayerIdentity?> ResolveAsync(Guid tenantId, string token, CancellationToken cancellationToken = default);
    Task<PlayerSessionResolution> ResolveWithStatusAsync(Guid tenantId, string token, CancellationToken cancellationToken = default);
    Task RevokeAsync(string token, CancellationToken cancellationToken = default);
}

internal sealed record PlayerSessionResolution(PlayerIdentity? Player, bool IsBanned, string? SessionKey = null);

/// <summary>Opaque key for revalidating the sign-in session that opened a stream.</summary>
internal sealed record PlayerStreamSession(string Key);

internal sealed class PlayerSessionService(DatabaseSources sources, ITenantDb tenantDb, IClock clock, ILocks locks) : IPlayerSessionIssuer, IPlayerSessionResolver
{
    internal const string CookieName = "__Host-qp";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public async Task IssueAccountAsync(HttpContext context, Guid tenantId, Guid accountId, CancellationToken cancellationToken = default)
    {
        var token = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(token);
        await tenantDb.InTenantAsync(tenantId, async transaction =>
        {
            var candidate = await transaction.QuerySingleOrDefaultAsync<Guid?>("""
                select id from players where account_id = @accountId and deleted_at is null
                """, new { accountId }, cancellationToken);
            if (candidate is null) throw new InvalidOperationException("The account is not available.");

            await locks.PlayersAsync(transaction, [candidate.Value], cancellationToken);
            var player = await transaction.QuerySingleOrDefaultAsync<Guid?>("""
                select id from players
                where id = @playerId and account_id = @accountId and deleted_at is null and banned_at is null
                """, new { playerId = candidate.Value, accountId }, cancellationToken);
            if (player is null) throw new InvalidOperationException("The account is not available.");

            await InsertAccountSessionAsync(tenantId, accountId, hash, cancellationToken);
            return 0;
        }, cancellationToken: cancellationToken);
        WriteCookie(context, token);
    }

    public async Task IssueGuestAsync(HttpContext context, Guid tenantId, Guid playerId, CancellationToken cancellationToken = default)
    {
        var token = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(token);
        await tenantDb.InTenantAsync(tenantId, async transaction =>
        {
            // This key-share lock serializes guest issuance with the purge's FOR UPDATE
            // eligibility lock. A session either commits before the sweep rechecks it, or
            // observes the tombstone and is not issued. Players are tenant-owned rows, so
            // this lock must use the tenant app role rather than the system session role.
            // Retain the existing live-player behavior for guests that have since been
            // attached to an account; the upgrade flow relies on this session-recovery
            // path. A purge tombstone is the only unavailable state.
            var player = await transaction.QuerySingleOrDefaultAsync<Guid?>("""
                select id from players
                where id = @playerId and deleted_at is null and banned_at is null
                for key share
                """, new { playerId }, cancellationToken);
            if (player is null) throw new InvalidOperationException("The guest player is not available.");

            await using var connection = new Npgsql.NpgsqlConnection(sources.SystemConnectionString);
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("""
                insert into auth_sessions (token_hash, tenant_id, guest_id, expires_at)
                values (@hash, @tenantId, @playerId, now() + interval '30 days')
                """, new { hash, tenantId, playerId }, cancellationToken: cancellationToken));
            return 0;
        }, cancellationToken: cancellationToken);
        WriteCookie(context, token);
    }

    public async Task<PlayerIdentity?> ResolveAsync(Guid tenantId, string token, CancellationToken cancellationToken = default)
        => (await ResolveWithStatusAsync(tenantId, token, cancellationToken)).Player;

    public async Task<PlayerSessionResolution> ResolveWithStatusAsync(Guid tenantId, string token, CancellationToken cancellationToken = default)
    {
        if (!TryTokenHash(token, out var hash)) return new(null, false);
        await using var connection = new Npgsql.NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        var session = await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition("""
            select tenant_id as TenantId, account_id as AccountId, guest_id as GuestId, revoked_at as RevokedAt
            from auth_sessions
            where token_hash = @hash and tenant_id = @tenantId and expires_at > now()
            """, new { hash, tenantId }, cancellationToken: cancellationToken));
        if (session is null) return new(null, false);

        if (session.GuestId is { } guestId)
        {
            var player = await PlayerAsync(tenantId, guestId, cancellationToken);
            if (player is null) return new(null, false);
            if (player.BannedAt is not null) return new(null, true);
            return new(session.RevokedAt is null ? new PlayerIdentity(tenantId, player.Id, null, true) : null, false,
                session.RevokedAt is null ? Convert.ToHexString(hash) : null);
        }
        if (session.AccountId is not { } accountId) return new(null, false);
        var account = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "select id from accounts where id = @accountId and deleted_at is null", new { accountId }, cancellationToken: cancellationToken));
        if (account is null) return new(null, false);
        var accountPlayer = await AccountPlayerAsync(tenantId, accountId, cancellationToken);
        if (accountPlayer is null) return new(null, false);
        if (accountPlayer.BannedAt is not null) return new(null, true);
        return new(session.RevokedAt is null ? new PlayerIdentity(tenantId, accountPlayer.Id, accountId, false) : null, false,
            session.RevokedAt is null ? Convert.ToHexString(hash) : null);
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!TryTokenHash(token, out var hash)) return;
        await using var connection = new Npgsql.NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("delete from auth_sessions where token_hash = @hash", new { hash }, cancellationToken: cancellationToken));
    }

    private Task<PlayerRow?> PlayerAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken) =>
        tenantDb.InTenantAsync(tenantId, transaction => transaction.QuerySingleOrDefaultAsync<PlayerRow>("""
            select id as Id, banned_at as BannedAt from players where id = @playerId and deleted_at is null
            """, new { playerId }, cancellationToken), cancellationToken: cancellationToken, readOnly: true);

    private Task<PlayerRow?> AccountPlayerAsync(Guid tenantId, Guid accountId, CancellationToken cancellationToken) =>
        tenantDb.InTenantAsync(tenantId, transaction => transaction.QuerySingleOrDefaultAsync<PlayerRow>("""
            select id as Id, banned_at as BannedAt from players where account_id = @accountId and deleted_at is null
            """, new { accountId }, cancellationToken), cancellationToken: cancellationToken, readOnly: true);

    private async Task InsertAccountSessionAsync(Guid tenantId, Guid accountId, byte[] hash, CancellationToken cancellationToken)
    {
        await using var connection = new Npgsql.NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var account = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
            select id from accounts where id = @accountId and deleted_at is null for update
            """, new { accountId }, transaction, cancellationToken: cancellationToken));
        if (account is null) throw new InvalidOperationException("The account is not available.");
        await connection.ExecuteAsync(new CommandDefinition("""
            insert into auth_sessions (token_hash, tenant_id, account_id, expires_at)
            values (@hash, @tenantId, @accountId, now() + interval '30 days')
            """, new { hash, tenantId, accountId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
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

    private sealed record SessionRow(Guid TenantId, Guid? AccountId, Guid? GuestId, DateTimeOffset? RevokedAt);
    private sealed record PlayerRow(Guid Id, DateTimeOffset? BannedAt);
}

internal sealed class PlayerSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IPlayerSessionResolver sessions)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is not null && context.Request.Cookies.TryGetValue(PlayerSessionService.CookieName, out var token))
        {
            var resolution = await sessions.ResolveWithStatusAsync(tenant.Id, token, context.RequestAborted);
            if (resolution.IsBanned) context.Items[typeof(BannedPlayer)] = BannedPlayer.Value;
            if (resolution.Player is { } player)
            {
                context.Items[typeof(PlayerIdentity)] = player;
                if (resolution.SessionKey is { } sessionKey) context.Items[typeof(PlayerStreamSession)] = new PlayerStreamSession(sessionKey);
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, player.PlayerId.ToString()), new Claim("tenant", player.TenantId.ToString())],
                    PlayerSessionService.CookieName));
            }
        }
        await next(context);
    }
}

internal sealed class DatabaseXmlRepository(DatabaseSources sources) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = new Npgsql.NpgsqlConnection(sources.SystemConnectionString);
        connection.Open();
        return connection.Query<string>("select xml from data_protection_keys order by id").Select(XElement.Parse).ToArray();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = new Npgsql.NpgsqlConnection(sources.SystemConnectionString);
        connection.Open();
        connection.Execute("insert into data_protection_keys (friendly_name, xml) values (@friendlyName, @xml)", new { friendlyName, xml = element.ToString(SaveOptions.DisableFormatting) });
    }
}

internal sealed class PlayerAntiforgery(IDataProtectionProvider protection, IClock clock)
{
    private const string Purpose = "quesshi.player-antiforgery.v1";
    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    public string Issue(Guid tenantId, PlayerIdentity? player)
    {
        var payload = new AntiforgeryPayload(tenantId, player?.Subject ?? "anonymous", clock.Now, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
        return _protector.Protect(JsonSerializer.Serialize(payload));
    }

    public string Issue(HttpContext context)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("Antiforgery requires tenant resolution.");
        return Issue(tenant.Id, context.Player());
    }

    public bool IsValid(Guid tenantId, PlayerIdentity? player, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<AntiforgeryPayload>(_protector.Unprotect(token));
            return payload is not null
                && payload.TenantId == tenantId
                && payload.Subject == (player?.Subject ?? "anonymous")
                && payload.IssuedAt >= clock.Now.AddDays(-1);
        }
        catch (CryptographicException) { return false; }
        catch (JsonException) { return false; }
    }

    public bool IsValid(HttpContext context, string? token)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        return tenant is not null && IsValid(tenant.Id, context.Player(), token);
    }

    private sealed record AntiforgeryPayload(Guid TenantId, string Subject, DateTimeOffset IssuedAt, string Nonce);
}

internal sealed class ConfigureDataProtectionKeyRing(DatabaseXmlRepository repository) : IPostConfigureOptions<KeyManagementOptions>
{
    public void PostConfigure(string? name, KeyManagementOptions options) => options.XmlRepository = repository;
}

internal static class PlayerSecurityApplicationBuilderExtensions
{
    public static IServiceCollection AddPlayerSecurity(this IServiceCollection services)
    {
        services.AddSingleton<DatabaseXmlRepository>();
        services.AddSingleton<IPostConfigureOptions<KeyManagementOptions>, ConfigureDataProtectionKeyRing>();
        services.AddDataProtection().SetApplicationName("quesshi.platform");
        services.AddSingleton<PlayerSessionService>();
        services.AddSingleton<IPlayerSessionIssuer>(provider => provider.GetRequiredService<PlayerSessionService>());
        services.AddSingleton<IPlayerSessionResolver>(provider => provider.GetRequiredService<PlayerSessionService>());
        services.AddSingleton<PlayerAntiforgery>();
        return services;
    }

    public static IApplicationBuilder UsePlayerSessionAuthentication(this IApplicationBuilder app) => app.UseMiddleware<PlayerSessionMiddleware>();

    internal static PlayerIdentity? Player(this HttpContext context) => context.Items.TryGetValue(typeof(PlayerIdentity), out var value) ? value as PlayerIdentity : null;

    internal static bool IsBannedPlayer(this HttpContext context) => context.Items.ContainsKey(typeof(BannedPlayer));
}

internal sealed class BannedPlayer
{
    public static BannedPlayer Value { get; } = new();
}
