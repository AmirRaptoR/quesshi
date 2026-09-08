using System.Text.Json;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;

namespace Quesshi.Server.Tests;

/// <summary>
/// A minimal, from-scratch <see cref="IGrainStorage"/> standing in for the real "hot" Redis provider,
/// used only by <see cref="MatchRecoveryClusterFixture"/>'s own isolated silo. Orleans's built-in
/// <c>MemoryGrainStorage</c> has no seam for injecting a write failure or for planting a row shaped
/// like one written by code that predates a field, so this exists purely to give the durable
/// settlement tests two things a real storage failure and a real pre-upgrade record would give them:
/// <see cref="FailNextWriteFor"/> to make one specific grain's next <c>WriteStateAsync</c> throw, and
/// <see cref="Seed"/> to plant an arbitrary <typeparamref name="object"/> — most usefully a
/// <c>MatchStateRecord</c> with <c>SettlementJson</c> left at its default "" — directly into storage
/// before the grain is ever activated.
/// </summary>
public sealed class FaultyMatchStorage : IGrainStorage
{
    private readonly Dictionary<string, string> _rows = [];
    private readonly Dictionary<string, int> _failAfter = [];

    /// <summary>
    /// Arranges for one specific future <c>WriteStateAsync</c> for this exact grain to throw instead of
    /// writing, then reverts to succeeding — the storage-layer analogue of FakePlayers.FailNextUpsertFor,
    /// standing in for a Redis write that fails independently of whatever Mongo effect it followed.
    /// <paramref name="afterSuccessfulWrites"/> lets a test target a write deep inside one grain call —
    /// MatchGrain.SettleAsync makes several in a row (the transition, then one checkpoint per settled
    /// participant, then a final one) — rather than only ever the very next one: 0 fails the next write,
    /// 1 lets the next write through and fails the one after it, and so on.
    /// </summary>
    public void FailNextWriteFor(GrainId grainId, int afterSuccessfulWrites = 0)
        => _failAfter[grainId.ToString()] = afterSuccessfulWrites;

    /// <summary>Plants a row directly, bypassing WriteStateAsync entirely — the only way to give a
    /// grain a state object shaped like one written before a field existed, since the field's own
    /// default is indistinguishable from "never written" by construction (see MatchStateRecord).</summary>
    public void Seed<T>(string stateName, GrainId grainId, T value)
        => _rows[Key(stateName, grainId)] = JsonSerializer.Serialize(value);

    private static string Key(string stateName, GrainId grainId) => $"{stateName}/{grainId}";

    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (_rows.TryGetValue(Key(stateName, grainId), out var json))
        {
            grainState.State = JsonSerializer.Deserialize<T>(json)!;
            grainState.RecordExists = true;
            grainState.ETag = "seeded";
        }
        return Task.CompletedTask;
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var key = grainId.ToString();
        if (_failAfter.TryGetValue(key, out var remaining))
        {
            if (remaining > 0)
            {
                _failAfter[key] = remaining - 1;
            }
            else
            {
                _failAfter.Remove(key);
                throw new InvalidOperationException($"Simulated storage failure writing {stateName}/{grainId}.");
            }
        }

        _rows[Key(stateName, grainId)] = JsonSerializer.Serialize(grainState.State);
        grainState.RecordExists = true;
        grainState.ETag = "1";
        return Task.CompletedTask;
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        _rows.Remove(Key(stateName, grainId));
        grainState.RecordExists = false;
        grainState.ETag = null;
        return Task.CompletedTask;
    }
}
