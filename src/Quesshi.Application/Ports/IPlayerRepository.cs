using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public interface IPlayerRepository
{
    Task<Player?> GetAsync(string id, CancellationToken ct = default);
    Task<Player?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<IReadOnlyList<Player>> SearchAsync(string? text, int skip, int take, CancellationToken ct = default);
    Task<long> CountAsync(CancellationToken ct = default);
    Task UpsertAsync(Player player, CancellationToken ct = default);
}
