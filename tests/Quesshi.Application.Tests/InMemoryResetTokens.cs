using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class InMemoryResetTokens : IResetTokenStore
{
    private readonly Dictionary<string, PasswordResetToken> _items = [];

    public Task SaveAsync(PasswordResetToken token, CancellationToken ct = default)
    {
        _items[token.SecretHash] = token;
        return Task.CompletedTask;
    }

    public Task<PasswordResetToken?> GetAsync(string secretHash, CancellationToken ct = default)
        => Task.FromResult(_items.GetValueOrDefault(secretHash));
}
