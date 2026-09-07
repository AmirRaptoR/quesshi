namespace Quesshi.Application.Ports;

/// <summary>
/// Who is here right now, for the sole purpose of deciding who can be invited into a live duel this
/// instant — the random queue and friend challenges. Never consulted to decide the outcome of a duel;
/// abandonment there stays defined by silence, per the domain rules.
/// </summary>
public interface IPresence
{
    /// <summary>Marks a player online for <paramref name="ttl"/>. Called again — with a fresh TTL — on every heartbeat.</summary>
    Task MarkOnlineAsync(string playerId, TimeSpan ttl, CancellationToken ct = default);

    Task MarkOfflineAsync(string playerId, CancellationToken ct = default);

    /// <summary>The subset of <paramref name="playerIds"/> that is online, in one round trip regardless of how many ids are given.</summary>
    Task<IReadOnlyCollection<string>> OnlineAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default);
}
