using System.Diagnostics;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Bench;

/// <summary>
/// Wraps the real <see cref="IMatchArchive"/> so <c>GameEndpoints.ListMatchesAsync</c> can be called
/// unmodified while the bench harness still learns how long the one call it makes actually took.
/// Everything but <see cref="ForPlayerAsync"/> is a plain pass-through: the endpoint never calls them.
/// </summary>
internal sealed class TimingMatchArchive(IMatchArchive inner) : IMatchArchive
{
    /// <summary>Elapsed time of the most recent <see cref="ForPlayerAsync"/> call.</summary>
    public TimeSpan LastForPlayerElapsed { get; private set; }

    public async Task<IReadOnlyList<ArchivedMatch>> ForPlayerAsync(string playerId, int take, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await inner.ForPlayerAsync(playerId, take, ct);
        LastForPlayerElapsed = sw.Elapsed;
        return result;
    }

    public Task SaveAsync(ArchivedMatch match, CancellationToken ct = default) => inner.SaveAsync(match, ct);
    public Task<ArchivedMatch?> ByCodeAsync(string code, CancellationToken ct = default) => inner.ByCodeAsync(code, ct);
    public Task<long> CountAsync(CancellationToken ct = default) => inner.CountAsync(ct);
}

/// <summary>Same idea as <see cref="TimingMatchArchive"/>, for the one player lookup the endpoint makes.</summary>
internal sealed class TimingPlayerRepository(IPlayerRepository inner) : IPlayerRepository
{
    /// <summary>Elapsed time of the most recent <see cref="GetManyAsync"/> call.</summary>
    public TimeSpan LastGetManyElapsed { get; private set; }

    public async Task<IReadOnlyList<Player>> GetManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await inner.GetManyAsync(ids, ct);
        LastGetManyElapsed = sw.Elapsed;
        return result;
    }

    public Task<Player?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);
    public Task<Player?> GetByEmailAsync(string email, CancellationToken ct = default) => inner.GetByEmailAsync(email, ct);
    public Task<IReadOnlyList<Player>> SearchAsync(string? text, int skip, int take, CancellationToken ct = default) => inner.SearchAsync(text, skip, take, ct);
    public Task<long> CountAsync(CancellationToken ct = default) => inner.CountAsync(ct);
    public Task UpsertAsync(Player player, CancellationToken ct = default) => inner.UpsertAsync(player, ct);
}
