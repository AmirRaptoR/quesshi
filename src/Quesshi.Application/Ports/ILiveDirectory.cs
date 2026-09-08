namespace Quesshi.Application.Ports;

/// <summary>
/// One row of the in-flight live-duel index — everything <c>/admin/live</c> renders for one duel,
/// without activating its grain: code, every seated player, language, round <c>n</c> of total, phase
/// and when it started. <see cref="Participants"/> is in join order, exactly as
/// <c>LiveMatch.Participants</c> defines it — one entry for a lobby nobody has joined yet, more as
/// seats fill. This used to be a fixed <c>ChallengerId</c>/<c>string? OpponentId</c> pair, which is
/// exactly why a capacity>2 duel's third-and-later players never showed up in <c>/admin/live</c>: an
/// admin watching a four-player duel saw two names and nothing telling them two more were missing.
/// </summary>
public sealed record LiveDirectoryRow(
    string MatchId, string Code, List<string> Participants, int Lang,
    int RoundIndex, int TotalRounds, int Phase, DateTimeOffset StartedAt);

/// <summary>
/// The Redis-backed index of live duels currently in flight — the only way to answer "what is
/// playing this second" without enumerating grains, which cannot be done at all. Written by
/// <c>LiveMatchGrain</c> on every phase change and deleted when the duel ends, however it ends.
/// Also answers the dashboard's connected-players and queue-depth counters, both written elsewhere
/// (the presence/random-queue sub-issue) and only read here — a key nobody has written yet reads as
/// zero, which is why the dashboard renders correctly before that lands.
/// </summary>
public interface ILiveDirectory
{
    Task UpsertAsync(LiveDirectoryRow row, CancellationToken ct = default);
    Task RemoveAsync(string matchId, CancellationToken ct = default);
    Task<IReadOnlyList<LiveDirectoryRow>> AllAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<int> ConnectedCountAsync(CancellationToken ct = default);
    Task<int> QueueDepthAsync(CancellationToken ct = default);
}
