using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class FakeArchive : IMatchArchive
{
    public readonly List<ArchivedMatch> Items = [];

    /// <summary>Counterpart to FakePlayers.Queries; the list endpoint should read the archive once.</summary>
    public int Queries;

    /// <summary>Stands in for the round trip to Mongo, as in FakePlayers.</summary>
    public int DelayMs;

    /// <summary>
    /// Guards every touch of <see cref="Items"/> that happens from inside this class. Real grain
    /// activations — not just the test method's own call stack — can call in here: a live duel's
    /// safety-net reminder or timer left running from an earlier test in the same
    /// <c>LiveClusterCollection</c> fires against the one shared <see cref="LiveShared"/> clock and can
    /// still be settling while the current test's own duel is settling too, and <see cref="List{T}"/>
    /// is not safe under two such calls landing at once. Settlement wiring (issue #48) is what first
    /// made that overlap long enough to hit in practice, not something particular to any one test.
    /// </summary>
    private readonly object _lock = new();

    public void ResetCounters() => Queries = 0;

    public Task SaveAsync(ArchivedMatch m, CancellationToken ct = default)
    {
        lock (_lock) { Items.RemoveAll(x => x.Id == m.Id); Items.Add(m); }
        return Task.CompletedTask;
    }

    public Task<ArchivedMatch?> ByCodeAsync(string code, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(Items.FirstOrDefault(m => m.Code == code));
    }

    public async Task<IReadOnlyList<ArchivedMatch>> ForPlayerAsync(string p, int take, CancellationToken ct = default)
    {
        Queries++;
        if (DelayMs > 0) await Task.Delay(DelayMs, ct);
        // Mirrors MongoMatchArchive.ForPlayerAsync: live and async rows alike, newest first.
        lock (_lock)
            return [.. Items.Where(m => m.ChallengerId == p || m.OpponentId == p).OrderByDescending(m => m.CreatedAt).Take(take)];
    }

    public Task<long> CountAsync(CancellationToken ct = default) { lock (_lock) return Task.FromResult((long)Items.Count); }
    public Task<long> CountLiveAsync(CancellationToken ct = default) { lock (_lock) return Task.FromResult((long)Items.Count(m => m.IsLive)); }

    /// <summary>
    /// The two-entry <see cref="ParticipantResult"/> list every test written before N-player
    /// participants existed needs — the shape <c>ChallengerScore</c>/<c>OpponentScore</c> used to carry
    /// positionally, now <c>Results[0]</c>/<c>Results[1]</c>. Place and outcome are the same "not yet
    /// ranked" placeholder <c>MatchGrain</c>/<c>LiveMatchGrain</c> use for a match that has not
    /// finished: no test in this project asserts on them, only on the scores the obsolete accessors
    /// still project.
    /// </summary>
    public static List<ParticipantResult> TestResults(string challengerId, string? opponentId, int challengerScore, int opponentScore) =>
        opponentId is null
            ? [new ParticipantResult(challengerId, challengerScore, 0, MatchOutcome.Loss)]
            : [new ParticipantResult(challengerId, challengerScore, 0, MatchOutcome.Loss),
               new ParticipantResult(opponentId, opponentScore, 0, MatchOutcome.Loss)];
}
