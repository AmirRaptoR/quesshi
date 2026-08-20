using System.Text.Json;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using StackExchange.Redis;

namespace Quesshi.Infrastructure.Redis;

/// <summary>
/// Reset tokens live in Redis keyed by the hash of the secret, with a TTL a little longer than the
/// token's own lifetime so a spent one can still answer "already used" before it disappears.
/// </summary>
public sealed class RedisResetTokenStore(IConnectionMultiplexer redis) : IResetTokenStore
{
    private static readonly TimeSpan Ttl = PasswordResetToken.Lifetime + TimeSpan.FromHours(1);

    private IDatabase Db => redis.GetDatabase();

    private static string Key(string secretHash) => $"quesshi:admin-reset:{secretHash}";

    public Task SaveAsync(PasswordResetToken token, CancellationToken ct = default)
        => Db.StringSetAsync(Key(token.SecretHash),
            JsonSerializer.Serialize(new PasswordResetTokenSnapshot(token.AdminUserId, token.SecretHash, token.IssuedAt, token.Used)),
            Ttl);

    public async Task<PasswordResetToken?> GetAsync(string secretHash, CancellationToken ct = default)
    {
        var raw = await Db.StringGetAsync(Key(secretHash));
        if (raw.IsNullOrEmpty) return null;

        var snapshot = JsonSerializer.Deserialize<PasswordResetTokenSnapshot>((string)raw!);
        return snapshot is null ? null : PasswordResetToken.Restore(snapshot.AdminUserId, snapshot.SecretHash, snapshot.IssuedAt, snapshot.Used);
    }
}
