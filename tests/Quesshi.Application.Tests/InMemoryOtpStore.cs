using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class InMemoryOtpStore : IOtpStore
{
    private readonly Dictionary<string, OtpChallenge> _items = [];
    public Task SaveAsync(OtpChallenge c, CancellationToken ct = default) { _items[c.Email] = c; return Task.CompletedTask; }
    public Task<OtpChallenge?> GetAsync(string email, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault(email.Trim().ToLowerInvariant()));
    public Task DeleteAsync(string email, CancellationToken ct = default) { _items.Remove(email.Trim().ToLowerInvariant()); return Task.CompletedTask; }
}
