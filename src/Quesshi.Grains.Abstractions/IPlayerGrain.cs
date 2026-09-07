using Orleans;

namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.IPlayerGrain")]
public interface IPlayerGrain : IGrainWithStringKey
{
    [Alias("ApplyResultAsync")]
    Task ApplyResultAsync(int outcome, int score, List<string> categoryIds, List<bool> correct);
    /// <summary>
    /// Records a live duel walked away from and returns the escalating penalty it costs, per
    /// <see cref="Quesshi.Domain.LiveRules.AbandonmentPenalty"/> and the rolling
    /// <see cref="Quesshi.Domain.LiveRules.AbandonmentWindow"/>. The caller is responsible for taking
    /// the same amount off the leaderboard; this only charges the player's own record.
    /// </summary>
    [Alias("RecordAbandonmentAsync")]
    Task<int> RecordAbandonmentAsync(DateTimeOffset now);
    [Alias("AddFriendAsync")]
    Task AddFriendAsync(string otherId);
    [Alias("RemoveFriendAsync")]
    Task RemoveFriendAsync(string otherId);
    [Alias("CardAsync")]
    Task<PlayerCard?> CardAsync();
}
