using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Grains;

/// <summary>
/// The one grain, on key 0, that owns the random live queue and relays friend challenges. Renamed
/// from <c>LiveLobbyGrain</c> (issue #51) — see <see cref="ILiveMatchmakingGrain"/>'s own remarks for
/// why, and for why the queue keeps a single lock that a challenge no longer needs.
/// Unlike a challenge, a queue entry is worthless once it goes stale — <see cref="Ttl"/> matches
/// <c>LobbyHub.PresenceTtl</c> exactly, because a player whose presence has lapsed is a player whose
/// queue entry has lapsed too.
/// </summary>
public sealed class LiveMatchmakingGrain(
    [PersistentState("live-lobby", "hot")] IPersistentState<LiveMatchmakingState> state,
    QuestionSetBuilder builder,
    IIdFactory ids,
    IMatchArchive archive,
    IClock clock,
    ILobbyNotifier notifier,
    ILogger<LiveMatchmakingGrain> logger) : Grain, ILiveMatchmakingGrain
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private const int MaxCodeAttempts = 5;
    private static readonly TimeSpan MinimumDueTime = TimeSpan.FromMilliseconds(1);

    private readonly Dictionary<string, IGrainTimer> _challengeTimers = new();

    /// <summary>
    /// A challenge that was already overdue when this activation started is expired right away rather
    /// than waiting for a call to notice it; everything still alive gets its timer re-armed against
    /// the deadline recorded when it was sent. A challenge left over from before issue #51 — one with
    /// no <see cref="LiveChallengeView.LobbyId"/>, from when a challenge carried settings instead of a
    /// pointer — is treated the same way: it means nothing under the new model, so it is expired
    /// exactly as if its clock had already run out, rather than risking <see cref="AcceptAsync"/>
    /// trying to join an empty lobby id later. The queue's own stale entries are swept by
    /// <see cref="PruneTickAsync"/>, armed here too, so an empty bucket's count is never stuck stale.
    /// </summary>
    public override async Task OnActivateAsync(CancellationToken ct)
    {
        foreach (var challenge in state.State.Challenges.ToList())
        {
            if (challenge.ExpiresAt <= clock.Now || string.IsNullOrEmpty(challenge.LobbyId))
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

    public async Task<int> ChallengeAsync(string challengeId, string challengerId, string targetId, string lobbyId)
    {
        if (challengerId == targetId) return (int)LiveChallengeResult.SelfChallenge;

        // No exclusivity check here any more (see this grain's own remarks): the only thing worth
        // verifying is that the lobby being pointed at is real and still open for the invitation to
        // mean anything, and asking the lobby itself is the only way to know that without duplicating
        // its rules here.
        var lobby = GrainFactory.GetGrain<ILiveMatchGrain>(lobbyId);
        var view = await lobby.GetAsync(challengerId);
        if (view is null || view.Phase != (int)LivePhase.Lobby) return (int)LiveChallengeResult.NotFound;

        var sentAt = clock.Now;
        var challenge = new LiveChallengeView(challengeId, challengerId, targetId, lobbyId, view.Code,
            sentAt, view.CreatedAt + LiveRules.LobbyExpires);

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

        // Consumed before the join is attempted, not after: an invitation is a one-shot pointer, spent
        // the instant it is acted on, whether or not the seat is still there by the time this lands.
        RemoveAndDisarm(challenge);
        await state.WriteStateAsync();

        var lobby = GrainFactory.GetGrain<ILiveMatchGrain>(challenge.LobbyId);
        var joinResult = (LiveJoinResult)await lobby.JoinAsync(targetId);
        if (joinResult is LiveJoinResult.Joined or LiveJoinResult.AlreadyIn)
        {
            await SafeNotifyAsync(() => notifier.DuelReadyAsync(challenge.ChallengerId, challenge.LobbyId));
            await SafeNotifyAsync(() => notifier.DuelReadyAsync(challenge.TargetId, challenge.LobbyId));
            return new LiveChallengeAcceptResult((int)LiveChallengeResult.Accepted, challenge.LobbyId, challenge.ChallengerId);
        }

        // Taken, Full, Expired, SelfJoin or Unknown: the lobby moved on before this invitation was
        // spent. Both sides are told the same way regardless (ChallengeFailedAsync carries no reason
        // of its own — the challenger's banner just clears, exactly as before); it is only the
        // accepting target's own direct return value below that tells Full and Taken apart from each
        // other and from the pair not worth distinguishing, since that return is the one place issue
        // #53 needs "refused gracefully" to mean more than a single generic failure.
        await SafeNotifyAsync(() => notifier.ChallengeFailedAsync(challenge.ChallengerId, challengeId));
        await SafeNotifyAsync(() => notifier.ChallengeFailedAsync(challenge.TargetId, challengeId));

        var reason = joinResult switch
        {
            LiveJoinResult.Full => LiveChallengeResult.LobbyFull,
            LiveJoinResult.Taken => LiveChallengeResult.LobbyTaken,
            LiveJoinResult.Expired => LiveChallengeResult.Expired,
            _ => LiveChallengeResult.DuelFailed // SelfJoin, Unknown
        };
        return new LiveChallengeAcceptResult((int)reason, null, challenge.ChallengerId);
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

    public Task<IReadOnlyList<LiveChallengeView>> PendingForAsync(string playerId)
        => Task.FromResult<IReadOnlyList<LiveChallengeView>>(
            [.. state.State.Challenges.Where(c => c.TargetId == playerId && c.ExpiresAt > clock.Now).OrderBy(c => c.SentAt)]);

    /// <summary>Exactly what <c>LiveEndpoints.CreateAsync</c> does for a code-shared duel: build the
    /// question set, retry a colliding code, create then join. Any failure drops the duel and hands
    /// back null; the caller is responsible for telling both players. Used by the random queue only —
    /// a challenge no longer builds a duel this way, since it now points at a lobby that already
    /// exists (see <see cref="ChallengeAsync"/>).</summary>
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
            if (joinResult != LiveJoinResult.Joined) return null;

            // Join no longer starts the duel on its own (issue #104) — the random queue has to start
            // it explicitly, exactly as a shared-link lobby's owner presses Start. A failed start is
            // treated exactly like a failed join always was: return null, and the caller tells both
            // players the duel could not be built.
            return await grain.StartPairedAsync() ? matchId : null;
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
        => new(c.ChallengeId, c.ChallengerId, c.LobbyId, c.LobbyCode, c.ExpiresAt);

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
            logger.LogWarning(ex, "ILobbyNotifier threw in the live matchmaking grain; the grain keeps running.");
        }
    }
}
