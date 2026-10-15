using Dapper;
using Npgsql;
using Quesshi.Application.Identity;
using Quesshi.Domain;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Otp;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Identity;

/// <summary>Completes provider-validated identities against the global account store and tenant player state.</summary>
public sealed class ExternalIdentityService : IIdentity
{
    private readonly DatabaseSources _sources;
    private readonly EmailAuthService _emailAuth;

    public ExternalIdentityService(
        DatabaseSources sources,
        ITenantDb tenantDb,
        byte[] profileTokenSecret,
        TimeSpan profileTokenLifetime,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(tenantDb);
        ArgumentNullException.ThrowIfNull(profileTokenSecret);
        if (profileTokenLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(profileTokenLifetime));
        }

        _sources = sources;
        _emailAuth = new EmailAuthService(
            sources,
            tenantDb,
            profileTokenSecret,
            new EmailAuthOptions(1, 1, TimeSpan.FromMinutes(1), profileTokenLifetime),
            UnusedEmailCodeSender.Instance,
            timeProvider);
    }

    public async Task<ExternalIdentityCompletion> CompleteExternalAsync(
        ExternalIdentityTenant tenant,
        string provider,
        string subject,
        string? email,
        bool emailVerified,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (tenant.Id == Guid.Empty) throw new ArgumentException("A tenant is required.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(tenant.DefaultLanguage)) throw new ArgumentException("A tenant default language is required.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("An identity provider is required.", nameof(provider));
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("An identity subject is required.", nameof(subject));

        string? normalizedEmail = null;
        if (emailVerified)
        {
            if (string.IsNullOrWhiteSpace(email) || !EmailAddress.IsValid(email))
            {
                throw new ArgumentException("A verified identity requires a valid email address.", nameof(email));
            }

            normalizedEmail = EmailAddress.Normalize(email);
        }

        var accountId = await CompleteAccountAsync(provider, subject, normalizedEmail, cancellationToken);
        if (accountId is null)
        {
            return new ExternalIdentityCompletion(ExternalIdentityCompletionState.Unavailable);
        }

        var player = await _emailAuth.GetTenantPlayerAsync(tenant.Id, accountId.Value, cancellationToken);
        if (player.State == TenantPlayerState.Unavailable)
        {
            return new ExternalIdentityCompletion(ExternalIdentityCompletionState.Unavailable);
        }
        if (player.State == TenantPlayerState.Live)
        {
            return new ExternalIdentityCompletion(ExternalIdentityCompletionState.SignedIn, accountId);
        }

        var seed = await _emailAuth.GetLatestProfileAsync(accountId.Value, cancellationToken);
        return new ExternalIdentityCompletion(
            ExternalIdentityCompletionState.ProfileNeeded,
            accountId,
            new ExternalIdentityProfile(
                _emailAuth.CreateProfileToken(tenant.Id, accountId.Value),
                seed?.DisplayName,
                seed?.AvatarSeed,
                seed?.Language ?? tenant.DefaultLanguage));
    }

    private async Task<Guid?> CompleteAccountAsync(
        string provider,
        string subject,
        string? normalizedEmail,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // This serializes first completion for a subject while allowing different subjects
        // (including subjects from different providers) to complete independently.
        await connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(hashtext(@provider), hashtext(@subject))",
            new { provider, subject }, transaction, cancellationToken: cancellationToken));

        var existing = await connection.QuerySingleOrDefaultAsync<AccountIdentityRow>(new CommandDefinition("""
            select ai.account_id as "AccountId", a.deleted_at as "DeletedAt"
            from account_identities ai
            join accounts a on a.id = ai.account_id
            where ai.provider = @provider and ai.subject = @subject
            for update of a
            """, new { provider, subject }, transaction, cancellationToken: cancellationToken));
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing.DeletedAt is null
                ? existing.AccountId
                : null;
        }

        var accountId = Guid.NewGuid();
        if (normalizedEmail is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "insert into accounts (id) values (@accountId)",
                new { accountId }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            var linkedAccount = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
                insert into accounts (id, email)
                values (@accountId, @email)
                on conflict (email) do update set email = excluded.email
                    where accounts.deleted_at is null
                returning id
                """, new { accountId, email = normalizedEmail }, transaction, cancellationToken: cancellationToken));
            if (linkedAccount is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            accountId = linkedAccount.Value;
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            insert into account_identities (provider, subject, account_id)
            values (@provider, @subject, @accountId)
            """, new { provider, subject, accountId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return accountId;
    }

    private sealed record AccountIdentityRow(Guid AccountId, DateTime? DeletedAt);

    private sealed class UnusedEmailCodeSender : IEmailCodeSender
    {
        public static readonly UnusedEmailCodeSender Instance = new();

        public Task SendAsync(string email, string code, string language, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("External identity completion does not deliver email codes.");
    }
}
