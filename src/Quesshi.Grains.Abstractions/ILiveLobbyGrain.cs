namespace Quesshi.Grains.Abstractions;

/// <summary>
/// A single queue of players waiting for a live stranger, one instance on integer key 0 — the same
/// "one grain means no locking and no race" mechanism <c>IMatchmakingGrain</c> relies on, which is
/// also the entire mechanism behind two players enqueueing at the same instant never producing two
/// duels: Orleans runs this grain's calls one after another, never concurrently.
/// </summary>
[Alias("Quesshi.Grains.Abstractions.ILiveLobbyGrain")]
public interface ILiveLobbyGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Prunes, then looks for a waiting entry with the same language and question count belonging to
    /// somebody else. Found: removes it, builds the duel from that entry's categories and levels, and
    /// returns the new match id. Not found: replaces any existing entry for this player with a fresh
    /// one and returns null. If the duel cannot be built, neither player is left queued and null is
    /// returned to this caller too — <see cref="Quesshi.Application.Ports.ILobbyNotifier"/> is what
    /// tells both players it failed.
    /// </summary>
    [Alias("EnqueueAsync")]
    Task<string?> EnqueueAsync(string playerId, int lang, int questionCount, List<string> categories, List<int> levels);

    [Alias("LeaveAsync")]
    Task LeaveAsync(string playerId);

    [Alias("HeartbeatAsync")]
    Task HeartbeatAsync(string playerId);

    /// <summary>How many others (never the caller) are queued in the caller's own bucket (same
    /// language, same question count), after pruning.</summary>
    [Alias("WaitingCountAsync")]
    Task<int> WaitingCountAsync(string playerId, int lang, int questionCount);
}
