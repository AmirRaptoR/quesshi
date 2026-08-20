using System.Text.Json;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using StackExchange.Redis;

namespace Quesshi.Infrastructure.Redis;

/// <summary>OTP challenges live in Redis with a TTL, so an abandoned code cleans itself up.</summary>
public sealed class RedisOtpStore(IConnectionMultiplexer redis) : IOtpStore
{
    private IDatabase Db => redis.GetDatabase();

    private static string Key(string email) => $"quesshi:otp:{email.Trim().ToLowerInvariant()}";

    public Task SaveAsync(OtpChallenge challenge, CancellationToken ct = default)
        => Db.StringSetAsync(Key(challenge.Email), JsonSerializer.Serialize(challenge.ToSnapshot()), OtpChallenge.Lifetime);

    public async Task<OtpChallenge?> GetAsync(string email, CancellationToken ct = default)
    {
        var raw = await Db.StringGetAsync(Key(email));
        if (raw.IsNullOrEmpty) return null;

        var snapshot = JsonSerializer.Deserialize<OtpSnapshot>((string)raw!);
        return snapshot is null ? null : OtpChallenge.FromSnapshot(snapshot);
    }

    public Task DeleteAsync(string email, CancellationToken ct = default) => Db.KeyDeleteAsync(Key(email));
}
