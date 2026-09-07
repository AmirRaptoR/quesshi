using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Grains;

/// <summary>
/// The one grain, on key 0, that owns both doors into a live duel: the random queue and friend
/// challenges. A player's commitment — waiting, sent, or received — is decided here under one lock,
/// which is what makes "at most one at a time" true across both doors rather than something each
/// door has to trust the other to respect. Unlike a challenge, a queue entry is worthless once it goes
/// stale — <see cref="Ttl"/> matches <c>LobbyHub.PresenceTtl</c> exactly, because a player whose
/// presence has lapsed is a player whose queue entry has lapsed too.
/// </summary>
public sealed class LiveLobbyGrain(
    [PersistentState("live-lobby", "hot")] IPersistentState<LiveLobbyState> state,
    QuestionSetBuilder builder,
    IIdFactory ids,
    IMatchArchive archive,
    IClock clock,
    ILobbyNotifier notifier,
    ILogger<LiveLobbyGrain> logger) : Grain, ILiveLobbyGrain
{
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private const int MaxCodeAttempts = 5;
    private static readonly TimeSpan MinimumDueTime = TimeSpan.FromMilliseconds(1);

    private readonly Dictionary<string, IGrainTimer> _challengeTimers = new();

    /// <summary>
    /// A challenge that was already overdue when this activation started is expired right away rather
    /// than waiting for a call to notice it; everything still alive gets its timer re-armed against
    /// the deadline recorded when it was sent. The queue's own stale entries are swept by
    /// <see cref="PruneTickAsync"/>, armed here too, so an empty bucket's count is never stuck stale.
    /// </summary>
    public override async Task OnActivateAsync(CancellationToken ct)
    {
        foreach (var challenge in state.State.Challenges.ToList())
        {
            if (challenge.ExpiresAt <= clock.Now)
            {
                RemoveAndDisarm(challenge);
                await SafeNotifyAsync(() => notifier.ChallengeExpiredAsync(challenge.ChallengerId, challenge.ChallengeId));
            }
            else
            {
                ArmTimer(challenge);
            }
        }

        this.RegisterGrainTimer(PruneTickAsync, new GrainTimerCreationOptions
        {
            DueTime = Ttl,
            Period = Ttl,
            KeepAlive = true
        });

        await state.WriteStateAsync();
    }

    public async Task<string?> EnqueueAsync(string playerId, int lang, int questionCount, List<string> categories, List<int> levels)
    {
        Prune();

        // Committed to a challenge already: the queue is not a second door for this player right now.
        if (HasChallenge(playerId)) return null;

        var waiting = state.State.Waiting.FirstOrDefault(w => w.Lang == lang && w.QuestionCount == questionCount && w.PlayerId != playerId);
        if (waiting is not null)
        {
            state.State.Waiting.Remove(waiting);
            await state.WriteStateAsync();

            var matchId = await BuildDuelAsync(waiting.PlayerId, playerId, waiting.Lang, waiting.QuestionCount, waiting.Categories, waiting.Levels);
            if (matchId is null)
            {
                await SafeNotifyAsync(() => notifier.QueueFailedAsync(waiting.PlayerId));
                await SafeNotifyAsync(() => notifier.QueueFailedAsync(playerId));
                await NotifyBucketCountAsync(lang, questionCount); // a third party still waiting in this bucket just lost one of its two possible opponents
                return null;
            }

            await SafeNotifyAsync(() => notifier.MatchedAsync(waiting.PlayerId, matchId));
            await SafeNotifyAsync(() => notifier.MatchedAsync(playerId, matchId));
            await NotifyBucketCountAsync(lang, questionCount); // same: a third party's count just dropped by two
            return matchId;
        }

        state.State.Waiting.RemoveAll(w => w.PlayerId == playerId);
        state.State.Waiting.Add(new LiveQueueEntry(playerId, lang, questionCount, categories, levels, clock.Now));
        await state.WriteStateAsync();
        await NotifyBucketCountAsync(lang, questionCount);
        return null;
    }

    public async Task LeaveAsync(string playerId)
    {
        var removed = state.State.Waiting.FirstOrDefault(w => w.PlayerId == playerId);
        if (removed is null) return;

        state.State.Waiting.RemoveAll(w => w.PlayerId == playerId);
        await state.WriteStateAsync();
        await NotifyBucketCountAsync(removed.Lang, removed.QuestionCount);
    }

    public async Task HeartbeatAsync(string playerId)
    {
        var index = state.State.Waiting.FindIndex(w => w.PlayerId == playerId);
        if (index < 0) return;

        state.State.Waiting[index] = state.State.Waiting[index] with { QueuedAt = clock.Now };
        await state.WriteStateAsync();
    }

    public Task<int> WaitingCountAsync(string playerId, int lang, int questionCount)
    {
        Prune();
        return Task.FromResult(state.State.Waiting.Count(w => w.Lang == lang && w.QuestionCount == questionCount && w.PlayerId != playerId));
    }

    public async Task<int> ChallengeAsync(string challengeId, string challengerId, string targetId, int lang,
        int questionCount, List<string> categoryIds, List<int> levels)
    {
        // A queue entry nobody has refreshed in a while must not block a challenge that would
        // otherwise go through cleanly.
        Prune();

        if (challengerId == targetId) return (int)LiveChallengeResult.SelfChallenge;
        if (HasCommitment(challengerId)) return (int)LiveChallengeResult.CallerCommitted;
        if (HasCommitment(targetId)) return (int)LiveChallengeResult.TargetCommitted;

        var sentAt = clock.Now;
        var challenge = new LiveChallengeView(challengeId, challengerId, targetId, lang, questionCount,
            categoryIds, levels, sentAt, sentAt + ChallengeLifetime);

        state.State.Challenges.Add(challenge);
        await state.WriteStateAsync();
        ArmTimer(challenge);

        await SafeNotifyAsync(() => notifier.ChallengeReceivedAsync(targetId, ToNotice(challenge)));
        return (int)LiveChallengeResult.Sent;
    }

    public async Task<LiveChallengeAcceptResult> AcceptAsync(string challengeId, string targetId)
    {
        var challenge = state.State.Challenges.FirstOrDefault(c => c.ChallengeId == challengeId);
        if (challenge is null) return new LiveChallengeAcceptResult((int)LiveChallengeResult.NotFound, null, null);
        if (challenge.TargetId != targetId) return new LiveChallengeAcceptResult((int)LiveChallengeResult.NotYours, null, null);

        // A race between the timer and this call: the deadline has passed but the timer hasn't fired
        // yet in this activation's turn. Treat it exactly as an already-expired challenge.
        if (challenge.ExpiresAt <= clock.Now)
        {
            RemoveAndDisarm(challenge);
            await state.WriteStateAsync();
            await SafeNotifyAsync(() => notifier.ChallengeExpiredAsync(challenge.ChallengerId, challengeId));
            return new LiveChallengeAcceptResult((int)LiveChallengeResult.Expired, null, challenge.ChallengerId);
        }

        // Removed before the duel is built, not after: both commitments are freed the moment this
        // challenge is spent, whether or not building the duel goes on to succeed.
        RemoveAndDisarm(challenge);
        await state.WriteStateAsync();

        var matchId = await BuildDuelAsync(challenge.ChallengerId, challenge.TargetId, challenge.Lang,
            challenge.QuestionCount, challenge.CategoryIds, challenge.Levels);
        if (matchId is null)
        {
            await SafeNotifyAsync(() => notifier.ChallengeFailedAsync(challenge.ChallengerId, challengeId));
            await SafeNotifyAsync(() => notifier.ChallengeFailedAsync(challenge.TargetId, challengeId));
            return new LiveChallengeAcceptResult((int)LiveChallengeResult.DuelFailed, null, challenge.ChallengerId);
        }

        await SafeNotifyAsync(() => notifier.DuelReadyAsync(challenge.ChallengerId, matchId));
        await SafeNotifyAsync(() => notifier.DuelReadyAsync(challenge.TargetId, matchId));
        return new LiveChallengeAcceptResult((int)LiveChallengeResult.Accepted, matchId, challenge.ChallengerId);
    }

    public async Task<int> DeclineAsync(string challengeId, string targetId)
    {
        var challenge = state.State.Challenges.FirstOrDefault(c => c.ChallengeId == challengeId);
        if (challenge is null) return (int)LiveChallengeResult.NotFound;
        if (challenge.TargetId != targetId) return (int)LiveChallengeResult.NotYours;

        RemoveAndDisarm(challenge);
        await state.WriteStateAsync();
        await SafeNotifyAsync(() => notifier.ChallengeDeclinedAsync(challenge.ChallengerId, challengeId));
        return (int)LiveChallengeResult.Declined;
    }

    public Task<LiveChallengeView?> PendingForAsync(string playerId)
        => Task.FromResult(state.State.Challenges.FirstOrDefault(c => c.TargetId == playerId && c.ExpiresAt > clock.Now));

    private bool HasChallenge(string playerId)
        => state.State.Challenges.Any(c => c.ChallengerId == playerId || c.TargetId == playerId);

    private bool HasCommitment(string playerId)
        => state.State.Waiting.Any(w => w.PlayerId == playerId) || HasChallenge(playerId);

    /// <summary>Exactly what <c>LiveEndpoints.CreateAsync</c> does for a code-shared duel: build the
    /// question set, retry a colliding code, create then join. Any failure drops the duel and hands
    /// back null; the caller is responsible for telling both players. Shared by both doors — a
    /// challenge's own settings on accept, or the first-queued player's settings on a match.</summary>
    private async Task<string?> BuildDuelAsync(string challengerId, string opponentId, int lang, int questionCount,
        List<string> categoryIds, List<int> levelInts)
    {
        List<Question> set;
        try
        {
            // Anything outside 1..5 is dropped rather than rejected: a nonsense level is the same
            // request as no level at all.
            var levels = levelInts.Where(l => l is >= 1 and <= 5).Select(l => (Difficulty)l).ToList();
            set = [.. await builder.BuildAsync((Language)lang, categoryIds, questionCount, levels)];
        }
        catch (NotEnoughQuestionsException)
        {
            return null;
        }

        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var code = ids.NewMatchCode();
            if (await archive.ByCodeAsync(code) is not null) continue;

            var matchId = ids.NewId();
            var grain = GrainFactory.GetGrain<ILiveMatchGrain>(matchId);
            await grain.CreateAsync(code, lang, challengerId, [.. set.Select(q => q.Id)]);

            var joinResult = (LiveJoinResult)await grain.JoinAsync(opponentId);
            return joinResult == LiveJoinResult.Joined ? matchId : null;
        }

        return null;
    }

    private void ArmTimer(LiveChallengeView challenge)
    {
        var due = challenge.ExpiresAt - clock.Now;
        if (due < MinimumDueTime) due = MinimumDueTime;

        _challengeTimers[challenge.ChallengeId] = this.RegisterGrainTimer(_ => OnExpireAsync(challenge.ChallengeId), new GrainTimerCreationOptions
        {
            DueTime = due,
            Period = Timeout.InfiniteTimeSpan,
            KeepAlive = true
        });
    }

    private async Task OnExpireAsync(string challengeId)
    {
        var challenge = state.State.Challenges.FirstOrDefault(c => c.ChallengeId == challengeId);
        if (challenge is null) return; // already accepted, declined, or expired by another path

        RemoveAndDisarm(challenge);
        await state.WriteStateAsync();
        await SafeNotifyAsync(() => notifier.ChallengeExpiredAsync(challenge.ChallengerId, challengeId));
    }

    private void RemoveAndDisarm(LiveChallengeView challenge)
    {
        state.State.Challenges.RemoveAll(c => c.ChallengeId == challenge.ChallengeId);
        if (_challengeTimers.Remove(challenge.ChallengeId, out var timer)) timer.Dispose();
    }

    private static LiveChallengeNotice ToNotice(LiveChallengeView c)
        => new(c.ChallengeId, c.ChallengerId, c.Lang, c.QuestionCount, c.CategoryIds, c.Levels, c.ExpiresAt);

    private async Task PruneTickAsync(CancellationToken ct)
    {
        var buckets = state.State.Waiting.Select(w => (w.Lang, w.QuestionCount)).Distinct().ToList();
        if (!Prune()) return;

        await state.WriteStateAsync();
        foreach (var (lang, questionCount) in buckets)
            await NotifyBucketCountAsync(lang, questionCount);
    }

    /// <summary>A queue entry nobody has refreshed for <see cref="Ttl"/> is gone — never counted,
    /// never handed to an arriving player as an opponent.</summary>
    private bool Prune()
    {
        var cutoff = clock.Now - Ttl;
        return state.State.Waiting.RemoveAll(w => w.QueuedAt < cutoff) > 0;
    }

    private async Task NotifyBucketCountAsync(int lang, int questionCount)
    {
        var bucket = state.State.Waiting.Where(w => w.Lang == lang && w.QuestionCount == questionCount).ToList();
        foreach (var entry in bucket)
            await SafeNotifyAsync(() => notifier.QueueCountChangedAsync(entry.PlayerId, bucket.Count - 1));
    }

    private async Task SafeNotifyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ILobbyNotifier threw in the live lobby; the lobby keeps running.");
        }
    }
}
