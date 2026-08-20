using Orleans;

namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.IPlayerGrain")]
public interface IPlayerGrain : IGrainWithStringKey
{
    [Alias("ApplyResultAsync")]
    Task ApplyResultAsync(int outcome, int score, List<string> categoryIds, List<bool> correct);
    [Alias("AddFriendAsync")]
    Task AddFriendAsync(string otherId);
    [Alias("RemoveFriendAsync")]
    Task RemoveFriendAsync(string otherId);
    [Alias("CardAsync")]
    Task<PlayerCard?> CardAsync();
}
