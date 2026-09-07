using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Quesshi.Grains.Abstractions;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Grains;

/// <summary>
/// The live counterpart to <see cref="MatchmakingGrain"/>: one instance on key 0, so two players
/// hitting "random live opponent" at the same instant are handled one after the other by Orleans'
/// single-threaded activation, never concurrently. Unlike the async queue, an entry here is worthless
/// after a minute — <see cref="Ttl"/> matches <c>LobbyHub.PresenceTtl</c> exactly, because a player
/// whose presence has lapsed is a player whose queue entry has lapsed.
/// </summary>
public sealed class LiveLobbyGrain(
    [PersistentState("live-lobby", "hot")] IPersistentState<LiveLobbyState> state,
    QuestionSetBuilder builder,
    IIdFactory ids,
    IMatchArchive archive,
    ILobbyNotifier notifier,
    IClock clock,
    ILogger<LiveLobbyGrain> logger) : Grain, ILiveLobbyGrain
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private const int MaxCodeAttempts = 5;

    private IGrainTimer? _timer;

    public override Task OnActivateAsync(CancellationToken ct)
    {
        _timer = this.RegisterGrainTimer(PruneTickAsync, new GrainTimerCreationOptions
        {
            DueTime = Ttl,
            Period = Ttl,
            KeepAlive = true
        });
        return Task.CompletedTask;
    }

    public async Task<string?> EnqueueAsync(string playerId, int lang, int questionCount, List<string> categories, List<int> levels)
    {
        Prune();

        var waiting = state.State.Waiting.FirstOrDefault(w => w.Lang == lang && w.QuestionCount == questionCount && w.PlayerId != playerId);
        if (waiting is not null)
        {
            state.State.Waiting.Remove(waiting);
            await state.WriteStateAsync();

            var matchId = await TryCreateDuelAsync(waiting, playerId);
            if (matchId is null)
            {
                await SafeNotifyAsync(() => notifier.QueueFailedAsync(waiting.PlayerId));
                await SafeNotifyAsync(() => notifier.QueueFailedAsync(playerId));
                return null;
            }

            await SafeNotifyAsync(() => notifier.MatchedAsync(waiting.PlayerId, matchId));
            await SafeNotifyAsync(() => notifier.MatchedAsync(playerId, matchId));
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

    /// <summary>Builds the duel from the waiting player's settings, the same steps
    /// <c>LiveEndpoints.CreateAsync</c> takes: build the question set, mint a collision-free code,
    /// create the grain and join the arriving player into it. Anything but a clean join is a failed
    /// match attempt, not a half-open duel.</summary>
    private async Task<string?> TryCreateDuelAsync(LiveQueueEntry waiting, string arrivingPlayerId)
    {
        List<Question> set;
        try
        {
            var categories = waiting.Categories.Count == 0 ? null : waiting.Categories;
            var levels = waiting.Levels.Count == 0 ? null : waiting.Levels.Select(l => (Difficulty)l).ToList();
            set = [.. await builder.BuildAsync((Language)waiting.Lang, categories, waiting.QuestionCount, levels)];
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
            await grain.CreateAsync(code, waiting.Lang, waiting.PlayerId, [.. set.Select(q => q.Id)]);

            var joinResult = (LiveJoinResult)await grain.JoinAsync(arrivingPlayerId);
            return joinResult == LiveJoinResult.Joined ? matchId : null;
        }

        return null;
    }

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
            logger.LogWarning(ex, "ILobbyNotifier threw for the live queue; the queue keeps running.");
        }
    }
}
