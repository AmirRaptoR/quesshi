using Microsoft.Extensions.Logging;
using Quesshi.Grains.Abstractions;
using Orleans.Concurrency;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Grains;

/// <summary>
/// Serialises all writes to one player's record. Two matches resolving at the same moment would
/// otherwise race on the same Mongo document and lose a result.
/// </summary>
public sealed class PlayerGrain(IPlayerRepository players, ILeaderboard leaderboard, ILogger<PlayerGrain> logger) : Grain, IPlayerGrain
{
    private Player? _player;

    public override async Task OnActivateAsync(CancellationToken ct)
        => _player = await players.GetAsync(this.GetPrimaryKeyString(), ct);

    /// <summary>
    /// Every write path reads through this rather than the bare field, because a prior failed write
    /// on this same activation drops <see cref="_player"/> to null (see <see cref="UpsertOrInvalidateAsync"/>)
    /// without waiting for a fresh activation to pick it back up — "reload on next use" means the very
    /// next call, not the next time Orleans happens to activate this grain again.
    /// </summary>
    private async Task<Player?> EnsurePlayerAsync()
        => _player ??= await players.GetAsync(this.GetPrimaryKeyString());

    public async Task SettleMatchAsync(string matchId, int? outcome, int score, List<string> categoryIds, List<bool> correct, DateTimeOffset? abandonedAt)
    {
        var player = await EnsurePlayerAsync();
        if (player is null)
        {
            logger.LogWarning("Settlement for unknown player {Player}", this.GetPrimaryKeyString());
            return;
        }

        // TryRecordSettledMatch is the whole dedup story: it runs the stat mutation and remembers
        // matchId in the same call, so a repeat for a match already on record applies the result and
        // the penalty zero times. When it does apply, the mutation is written in the same UpsertAsync
        // as the marker — one Mongo write, so the marker and the effect it guards cannot come apart.
        if (player.TryRecordSettledMatch(matchId, () =>
            {
                if (outcome is { } o)
                {
                    player.RecordResult((MatchOutcome)o, score);
                    for (var i = 0; i < correct.Count && i < categoryIds.Count; i++)
                        player.RecordAnswer(categoryIds[i], correct[i]);
                }

                if (abandonedAt is { } at) player.RecordAbandonment(at);
            }))
        {
            await UpsertOrInvalidateAsync(player);
        }

        // Written unconditionally, outside the guard above, so a retry whose Mongo write already
        // landed still repairs a leaderboard whose Redis write failed last time. Repeating an
        // absolute SetAsync costs nothing, which is exactly why it is safe to call every time rather
        // than only when the guard says something changed. Guests stay off the leaderboard entirely.
        if (!player.IsGuest) await leaderboard.SetAsync(player.Id, player.Stats.TotalScore);
    }

    public async Task AddFriendAsync(string otherId)
    {
        var player = await EnsurePlayerAsync();
        if (player is null) return;
        player.AddFriend(otherId);
        await UpsertOrInvalidateAsync(player);
    }

    public async Task RemoveFriendAsync(string otherId)
    {
        var player = await EnsurePlayerAsync();
        if (player is null) return;
        player.RemoveFriend(otherId);
        await UpsertOrInvalidateAsync(player);
    }

    /// <summary>
    /// Shared by every write path: upsert, and on a failed or ambiguous write drop the cache so the
    /// next call reloads from the repository rather than trusting a mutation Mongo may never have
    /// stored — a Mongo timeout or a dropped connection doesn't say which. The exception is rethrown
    /// so the caller (and, for a settlement retry, the same activation) sees the failure and retries
    /// against a truthful cache instead of one that silently believes an unpersisted write.
    /// </summary>
    private async Task UpsertOrInvalidateAsync(Player player)
    {
        try
        {
            await players.UpsertAsync(player);
        }
        catch
        {
            _player = null;
            throw;
        }
    }

    [ReadOnly]
    public Task<PlayerCard?> CardAsync()
        => Task.FromResult(_player is null ? null : new PlayerCard(_player.Id, _player.DisplayName, _player.AvatarSeed));
}
