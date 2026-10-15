using Dapper;
using Npgsql;
using Quesshi.Domain;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Identity;

/// <summary>Completes provider-validated identities against the global account store.</summary>
public sealed class ExternalIdentityService(DatabaseSources sources) : IIdentity
{
    public async Task<ExternalIdentityCompletion> CompleteExternalAsync(
        string provider,
        string subject,
        string? email,
        bool emailVerified,
        CancellationToken cancellationToken = default)
    {
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

        await using var connection = new NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // This serializes first completion for a subject while allowing different subjects
        // (including subjects from different providers) to complete independently.
        await connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(hashtext(@provider), hashtext(@subject))",
            new { provider, subject }, transaction, cancellationToken: cancellationToken));

        var existing = await connection.QuerySingleOrDefaultAsync<AccountIdentityRow>(new CommandDefinition("""
            select ai.account_id as AccountId, a.deleted_at as DeletedAt
            from account_identities ai
            join accounts a on a.id = ai.account_id
            where ai.provider = @provider and ai.subject = @subject
            for update of a
            """, new { provider, subject }, transaction, cancellationToken: cancellationToken));
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing.DeletedAt is null
                ? new ExternalIdentityCompletion(ExternalIdentityCompletionState.Completed, existing.AccountId)
                : new ExternalIdentityCompletion(ExternalIdentityCompletionState.Unavailable);
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
                return new ExternalIdentityCompletion(ExternalIdentityCompletionState.Unavailable);
            }

            accountId = linkedAccount.Value;
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            insert into account_identities (provider, subject, account_id)
            values (@provider, @subject, @accountId)
            """, new { provider, subject, accountId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new ExternalIdentityCompletion(ExternalIdentityCompletionState.Completed, accountId);
    }

    private sealed record AccountIdentityRow(Guid AccountId, DateTimeOffset? DeletedAt);
}
