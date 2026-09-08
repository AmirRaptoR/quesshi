using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public interface IPlayerRepository
{
    Task<Player?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Every player in one query. A page that shows forty duels needs eighty names, and asking for
    /// them one at a time is what made the match list slow.
    /// </summary>
    Task<IReadOnlyList<Player>> GetManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default);
    Task<Player?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<IReadOnlyList<Player>> SearchAsync(string? text, int skip, int take, CancellationToken ct = default);
    Task<long> CountAsync(CancellationToken ct = default);
    Task UpsertAsync(Player player, CancellationToken ct = default);
}
