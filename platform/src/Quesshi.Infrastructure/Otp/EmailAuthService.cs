using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quesshi.Application.Identity;
using Quesshi.Domain;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Otp;

internal sealed class EmailAuthService
{
    private readonly DatabaseSources _sources;
    private readonly ITenantDb _tenantDb;
    private readonly byte[] _secret;
    private readonly EmailAuthOptions _options;
    private readonly IEmailCodeSender _sender;
    private readonly TimeProvider _timeProvider;

    public EmailAuthService(DatabaseSources sources, ITenantDb tenantDb, byte[] secret, EmailAuthOptions options, IEmailCodeSender sender, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        options.Validate();
        _sources = sources;
        _tenantDb = tenantDb;
        _secret = secret;
        _options = options;
        _sender = sender;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static byte[] SecretFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("SECRET_KEY");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("SECRET_KEY is required for email authentication.");
        }

        return Encoding.UTF8.GetBytes(value);
    }

    public async Task<bool> RequestCodeAsync(string email, string clientAddress, string language, CancellationToken cancellationToken = default)
    {
        if (!EmailAddress.IsValid(email))
        {
            return false;
        }

        var normalized = EmailAddress.Normalize(email);
        var code = OtpCode.Create();
        var hash = HashCode(code);
        await using var connection = new NpgsqlConnection(_sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RemoveExpiredLimitsAsync(connection, transaction, cancellationToken);
        var emailCount = await IncrementLimitAsync(connection, transaction, $"code:{normalized}", cancellationToken);
        var addressCount = await IncrementLimitAsync(connection, transaction, $"code-ip:{clientAddress}", cancellationToken);
        if (emailCount <= _options.EmailLimit && addressCount <= _options.AddressLimit)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                insert into login_codes (email, code_hash, expires_at)
                values (@email, @hash, now() + @lifetime)
                on conflict (email) do update set code_hash = excluded.code_hash, attempts = 0,
                    expires_at = excluded.expires_at, created_at = now()
                """, new { email = normalized, hash, lifetime = OtpCode.Lifetime }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        if (emailCount <= _options.EmailLimit && addressCount <= _options.AddressLimit)
        {
            await _sender.SendAsync(normalized, code, language, cancellationToken);
        }

        return true;
    }

    public async Task<Guid?> VerifyCodeAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        if (!EmailAddress.IsValid(email) || !OtpCode.IsValid(code))
        {
            return null;
        }

        var normalized = EmailAddress.Normalize(email);
        await using var connection = new NpgsqlConnection(_sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var consumed = await connection.QuerySingleAsync<bool>(new CommandDefinition("""
            with locked as (
                select email from login_codes
                where email = @email and expires_at > now() and attempts < @maximumAttempts
                for update
            ), consumed as (
                delete from login_codes
                where email = @email and code_hash = @hash and exists (select 1 from locked)
                returning 1
            ), failed as (
                update login_codes
                set attempts = attempts + 1,
                    expires_at = case when attempts + 1 >= @maximumAttempts then now() else expires_at end
                where email = @email and exists (select 1 from locked)
                    and not exists (select 1 from consumed)
                returning 1
            )
            select exists(select 1 from consumed)
            """, new { email = normalized, hash = HashCode(code), maximumAttempts = OtpCode.MaximumAttempts }, transaction, cancellationToken: cancellationToken));
        if (!consumed)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var accountId = await connection.QuerySingleAsync<Guid>(new CommandDefinition("""
            insert into accounts (id, email) values (@id, @email)
            on conflict (email) do update set email = excluded.email
            returning id
            """, new { id = Guid.NewGuid(), email = normalized }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return accountId;
    }

    public Task<TenantPlayer> GetTenantPlayerAsync(Guid tenantId, Guid accountId, CancellationToken cancellationToken = default) =>
        _tenantDb.InTenantAsync(tenantId, async transaction =>
        {
            var player = await transaction.QuerySingleOrDefaultAsync<PlayerRow>("""
                select id as Id, banned_at as BannedAt, deleted_at as DeletedAt
                from players where account_id = @accountId
                """, new { accountId }, cancellationToken);
            return player is null ? new TenantPlayer(TenantPlayerState.Missing) :
                player.BannedAt is null && player.DeletedAt is null
                    ? new TenantPlayer(TenantPlayerState.Live, player.Id)
                    : new TenantPlayer(TenantPlayerState.Unavailable);
        }, cancellationToken: cancellationToken, readOnly: true);

    public async Task<ProfileSeed?> GetLatestProfileAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_sources.SystemConnectionString);
        return await connection.QuerySingleOrDefaultAsync<ProfileSeed>(new CommandDefinition("""
            select display_name as DisplayName, avatar_seed as AvatarSeed, lang as Language
            from account_players(@accountId)
            order by last_seen_at desc nulls last
            limit 1
            """, new { accountId }, cancellationToken: cancellationToken));
    }

    public async Task<ProfileConfirmation> ConfirmProfileAsync(Guid tenantId, Guid accountId, string displayName, string avatarSeed, string language, CancellationToken cancellationToken = default)
    {
        if (!IsValidProfile(displayName, avatarSeed, language))
        {
            return new(ProfileConfirmationState.Invalid);
        }

        try
        {
            return await _tenantDb.InTenantAsync(tenantId, async transaction =>
            {
                await transaction.ExecuteAsync("select claim_account(@accountId)", new { accountId }, cancellationToken);
                var supported = await transaction.QuerySingleOrDefaultAsync<string>("""
                    select code from tenant_languages where code = @language
                    """, new { language }, cancellationToken);
                if (supported is null)
                {
                    return new ProfileConfirmation(ProfileConfirmationState.Invalid);
                }

                var existing = await transaction.QuerySingleOrDefaultAsync<PlayerRow>("""
                    select id as Id, banned_at as BannedAt, deleted_at as DeletedAt
                    from players where account_id = @accountId
                    """, new { accountId }, cancellationToken);
                if (existing is not null)
                {
                    return existing.BannedAt is null && existing.DeletedAt is null
                        ? new ProfileConfirmation(ProfileConfirmationState.Existing, existing.Id)
                        : new ProfileConfirmation(ProfileConfirmationState.Unavailable);
                }

                var id = Guid.NewGuid();
                await transaction.ExecuteAsync("""
                    insert into players (tenant_id, id, account_id, display_name, avatar_seed, lang)
                    values (@tenantId, @id, @accountId, @displayName, @avatarSeed, @language)
                    """, new { tenantId, id, accountId, displayName = displayName.Trim(), avatarSeed = avatarSeed.Trim(), language }, cancellationToken);
                return new ProfileConfirmation(ProfileConfirmationState.Created, id);
            }, cancellationToken: cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == "P0002")
        {
            return new(ProfileConfirmationState.Unavailable);
        }
    }

    public async Task<bool> IsProfileValidAsync(Guid tenantId, string displayName, string avatarSeed, string language, CancellationToken cancellationToken = default)
    {
        if (!IsValidProfile(displayName, avatarSeed, language))
        {
            return false;
        }

        return await _tenantDb.InTenantAsync(tenantId, transaction => transaction.QuerySingleOrDefaultAsync<string>("""
            select code from tenant_languages where code = @language
            """, new { language }, cancellationToken), cancellationToken: cancellationToken, readOnly: true) is not null;
    }

    public string CreateProfileToken(Guid tenantId, Guid accountId)
    {
        var payload = new ProfileToken(tenantId, accountId, _timeProvider.GetUtcNow().Add(_options.ProfileTokenLifetime).ToUnixTimeSeconds(), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{data}.{Convert.ToHexString(Sign(data))}";
    }

    public async Task<Guid?> ConsumeProfileTokenAsync(string token, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2 || !TryDecode(parts[0], out var payload) || payload.TenantId != tenantId || payload.ExpiresAt <= _timeProvider.GetUtcNow().ToUnixTimeSeconds())
        {
            return null;
        }

        byte[] supplied;
        try { supplied = Convert.FromHexString(parts[1]); }
        catch (FormatException) { return null; }
        if (!CryptographicOperations.FixedTimeEquals(supplied, Sign(parts[0])))
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(_sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RemoveExpiredLimitsAsync(connection, transaction, cancellationToken);
        var consumed = await connection.ExecuteScalarAsync<int?>(new CommandDefinition("""
            insert into auth_rate_limits (key, window_start, count)
            values (@key, to_timestamp(@expiresAt), 1)
            on conflict do nothing
            returning count
            """, new { key = $"profile:{payload.Nonce}", expiresAt = payload.ExpiresAt }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return consumed is 1 ? payload.AccountId : null;
    }

    private Task RemoveExpiredLimitsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition("""
            delete from auth_rate_limits
            where (key like 'profile:%' and window_start < now())
               or (key not like 'profile:%' and window_start < now() - @rateLimitWindow)
            """, new { rateLimitWindow = _options.RateLimitWindow }, transaction, cancellationToken: cancellationToken));

    private async Task<int> IncrementLimitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, CancellationToken cancellationToken) =>
        await connection.QuerySingleAsync<int>(new CommandDefinition("""
            insert into auth_rate_limits (key, window_start, count)
            values (@key, to_timestamp(floor(extract(epoch from now()) / @seconds) * @seconds), 1)
            on conflict (key, window_start) do update set count = auth_rate_limits.count + 1
            returning count
            """, new { key, seconds = (int)_options.RateLimitWindow.TotalSeconds }, transaction, cancellationToken: cancellationToken));

    private byte[] HashCode(string code) => HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(code));

    private byte[] Sign(string value) => HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(value));

    private static bool TryDecode(string data, out ProfileToken payload)
    {
        payload = default!;
        try
        {
            var padded = data.Replace('-', '+').Replace('_', '/').PadRight(data.Length + ((4 - data.Length % 4) % 4), '=');
            payload = JsonSerializer.Deserialize<ProfileToken>(Convert.FromBase64String(padded))!;
            return payload is not null && payload.TenantId != Guid.Empty && payload.AccountId != Guid.Empty && !string.IsNullOrEmpty(payload.Nonce);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    private static bool IsValidProfile(string? displayName, string? avatarSeed, string? language) =>
        displayName is not null && displayName.Trim().Length is >= 2 and <= 64 &&
        avatarSeed is not null && avatarSeed.Trim().Length is > 0 and <= 128 &&
        language is not null && language.Length is > 0 and <= 16;

    private sealed record PlayerRow(Guid Id, DateTimeOffset? BannedAt, DateTimeOffset? DeletedAt);
    private sealed record ProfileToken(Guid TenantId, Guid AccountId, long ExpiresAt, string Nonce);
}
