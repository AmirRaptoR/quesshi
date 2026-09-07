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
/// door has to trust the other to respect.
/// </summary>
public sealed class LiveLobbyGrain(
    [PersistentState("lobby", "hot")] IPersistentState<LiveLobbyState> state,
    QuestionSetBuilder builder,
    IIdFactory ids,
    IMatchArchive archive,
    IClock clock,
    ILobbyNotifier notifier,
    ILogger<LiveLobbyGrain> logger) : Grain, ILiveLobbyGrain
{
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(45);
    private const int MaxCodeAttempts = 5;
    private static readonly TimeSpan MinimumDueTime = TimeSpan.FromMilliseconds(1);

    private readonly Dictionary<string, IGrainTimer> _timers = new();

    /// <summary>
    /// A challenge that was already overdue when this activation started is expired right away rather
    /// than waiting for a call to notice it; everything still alive gets its timer re-armed against
    /// the deadline recorded when it was sent.
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

        await state.WriteStateAsync();
    }

    public async Task<int> EnqueueAsync(string playerId, int lang)
    {
        if (HasCommitment(playerId)) return (int)LiveChallengeResult.CallerCommitted;

        state.State.Waiting.Add(new LiveQueueEntry(playerId, lang, clock.Now));
        await state.WriteStateAsync();
        return (int)LiveChallengeResult.Sent;
    }

    public async Task LeaveQueueAsync(string playerId)
    {
        if (state.State.Waiting.RemoveAll(w => w.PlayerId == playerId) > 0)
            await state.WriteStateAsync();
    }

    public async Task<int> ChallengeAsync(string challengeId, string challengerId, string targetId, int lang,
        int questionCount, List<string> categoryIds, List<int> levels)
    {
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

        var matchId = await BuildDuelAsync(challenge);
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

    private bool HasCommitment(string playerId)
        => state.State.Waiting.Any(w => w.PlayerId == playerId)
        || state.State.Challenges.Any(c => c.ChallengerId == playerId || c.TargetId == playerId);

    /// <summary>Exactly what <c>LiveEndpoints.CreateAsync</c> does for a code-shared duel: build the
    /// question set, retry a colliding code, create then join. Any failure drops the duel and hands
    /// back null; the caller is responsible for telling both players.</summary>
    private async Task<string?> BuildDuelAsync(LiveChallengeView challenge)
    {
        List<Question> set;
        try
        {
            var levels = challenge.Levels
                .Where(l => l is >= 1 and <= 5)
                .Select(l => (Difficulty)l)
                .ToList();

            set = [.. await builder.BuildAsync((Language)challenge.Lang, challenge.CategoryIds, challenge.QuestionCount, levels)];
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
            await grain.CreateAsync(code, challenge.Lang, challenge.ChallengerId, [.. set.Select(q => q.Id)]);

            var joinResult = (LiveJoinResult)await grain.JoinAsync(challenge.TargetId);
            return joinResult == LiveJoinResult.Joined ? matchId : null;
        }

        return null;
    }

    private void ArmTimer(LiveChallengeView challenge)
    {
        var due = challenge.ExpiresAt - clock.Now;
        if (due < MinimumDueTime) due = MinimumDueTime;

        _timers[challenge.ChallengeId] = this.RegisterGrainTimer(_ => OnExpireAsync(challenge.ChallengeId), new GrainTimerCreationOptions
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
        if (_timers.Remove(challenge.ChallengeId, out var timer)) timer.Dispose();
    }

    private static LiveChallengeNotice ToNotice(LiveChallengeView c)
        => new(c.ChallengeId, c.ChallengerId, c.Lang, c.QuestionCount, c.CategoryIds, c.Levels, c.ExpiresAt);

    private async Task SafeNotifyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ILobbyNotifier threw in the live lobby; the lobby state keeps running.");
        }
    }
}
