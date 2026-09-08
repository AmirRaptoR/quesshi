using System.Text.Json;
using Quesshi.Application.Ports;
using StackExchange.Redis;

namespace Quesshi.Infrastructure.Redis;

/// <summary>
/// One Redis hash, field = match id, value = the serialized <see cref="LiveDirectoryRow"/> —
/// exactly the shape <c>/admin/live</c> renders, so the endpoint never has to call back into a
/// grain to answer "what is playing right now". <see cref="ConnectedCountAsync"/> and
/// <see cref="QueueDepthAsync"/> read the two sets the presence/queue sub-issue writes; this class
/// only reads them, and a set nobody has written yet has length zero.
/// </summary>
public sealed class RedisLiveDirectory(IConnectionMultiplexer redis) : ILiveDirectory
{
    private const string InFlightKey = "quesshi:live:inflight";
    private const string ConnectedKey = "quesshi:live:connected";
    private const string QueueKey = "quesshi:live:queue";

    private IDatabase Db => redis.GetDatabase();

    public Task UpsertAsync(LiveDirectoryRow row, CancellationToken ct = default)
        => Db.HashSetAsync(InFlightKey, row.MatchId, JsonSerializer.Serialize(row));

    public Task RemoveAsync(string matchId, CancellationToken ct = default)
        => Db.HashDeleteAsync(InFlightKey, matchId);

    public async Task<IReadOnlyList<LiveDirectoryRow>> AllAsync(CancellationToken ct = default)
    {
        var entries = await Db.HashGetAllAsync(InFlightKey);
        return [.. entries.Select(e => JsonSerializer.Deserialize<LiveDirectoryRow>((string)e.Value!)!)];
    }

    public async Task<int> CountAsync(CancellationToken ct = default) => (int)await Db.HashLengthAsync(InFlightKey);
    public async Task<int> ConnectedCountAsync(CancellationToken ct = default) => (int)await Db.SetLengthAsync(ConnectedKey);
    public async Task<int> QueueDepthAsync(CancellationToken ct = default) => (int)await Db.SetLengthAsync(QueueKey);
}
