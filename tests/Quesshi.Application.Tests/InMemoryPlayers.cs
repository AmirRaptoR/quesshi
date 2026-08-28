using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class InMemoryPlayers : IPlayerRepository
{
    public readonly List<Player> Items = [];
    public Task<IReadOnlyList<Player>> GetManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Player>>([.. Items.Where(p => ids.Contains(p.Id))]);

    public Task<Player?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(p => p.Id == id));
    public Task<Player?> GetByEmailAsync(string email, CancellationToken ct = default)
        => Task.FromResult(Items.FirstOrDefault(p => p.Email == email.Trim().ToLowerInvariant()));
    public Task<IReadOnlyList<Player>> SearchAsync(string? text, int skip, int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Player>>([.. Items.Where(p => text is null || p.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)).Skip(skip).Take(take)]);
    public Task<long> CountAsync(CancellationToken ct = default) => Task.FromResult((long)Items.Count);
    public Task UpsertAsync(Player p, CancellationToken ct = default) { Items.RemoveAll(x => x.Id == p.Id); Items.Add(p); return Task.CompletedTask; }
}
