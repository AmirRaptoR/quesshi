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
public sealed class PlayerGrain(IPlayerRepository players, ILogger<PlayerGrain> logger) : Grain, IPlayerGrain
{
    private Player? _player;

    public override async Task OnActivateAsync(CancellationToken ct)
        => _player = await players.GetAsync(this.GetPrimaryKeyString(), ct);

    public async Task ApplyResultAsync(int outcome, int score, List<string> categoryIds, List<bool> correct)
    {
        if (_player is null)
        {
            logger.LogWarning("Result for unknown player {Player}", this.GetPrimaryKeyString());
            return;
        }

        _player.RecordResult((MatchOutcome)outcome, score);
        for (var i = 0; i < correct.Count && i < categoryIds.Count; i++)
            _player.RecordAnswer(categoryIds[i], correct[i]);

        await players.UpsertAsync(_player);
    }

    public async Task AddFriendAsync(string otherId)
    {
        if (_player is null) return;
        _player.AddFriend(otherId);
        await players.UpsertAsync(_player);
    }

    public async Task RemoveFriendAsync(string otherId)
    {
        if (_player is null) return;
        _player.RemoveFriend(otherId);
        await players.UpsertAsync(_player);
    }

    [ReadOnly]
    public Task<PlayerCard?> CardAsync()
        => Task.FromResult(_player is null ? null : new PlayerCard(_player.Id, _player.DisplayName, _player.AvatarSeed));
}
