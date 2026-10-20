using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quesshi.Shared;

public interface IKind
{
    string Id { get; }
    int Version { get; }
    string Family { get; }
    bool Gradable { get; }
}

/// <summary>A JSON boundary for the engine. Every value crossing it is an object.</summary>
public interface IKindRuntime : IKind
{
    JsonElement Upgrade(int from, JsonElement payload);
    JsonElement UpgradeSnapshot(int from, JsonElement snapshot);
    IReadOnlyList<Problem> Validate(JsonElement payload, ValidateEnv env);
    JsonElement Resolve(JsonElement payload, ResolveContext context);
    JsonElement Card(JsonElement payload, JsonElement resolved);
    JsonElement Reveal(JsonElement payload, JsonElement resolved);
    JsonElement? Normalize(JsonElement payload, JsonElement resolved, JsonElement? answer);
    Grade Grade(JsonElement payload, JsonElement resolved, JsonElement? stored);
}

/// <summary>Optional generation capability supplied by a question kind.</summary>
public interface IGenerates
{
    string PromptFragment { get; }
    JsonElement CandidateSchema { get; }
    IReadOnlyList<Problem> PostCheck(JsonElement candidate);
    JsonElement ToPayload(JsonElement candidate);
}

public interface IKindRegistry
{
    IKindRuntime Get(string id, int version);
    IKindRuntime GetCurrent(string id);
    bool TryGet(string id, int version, out IKindRuntime? kind);
    bool TryGetCurrent(string id, out IKindRuntime? kind);
    bool TryGetGenerator(string id, int version, out IGenerates? generator);
    bool TryGetCurrentGenerator(string id, out IGenerates? generator);
}

public sealed class KindRegistry : IKindRegistry
{
    private readonly Dictionary<(string Id, int Version), IKindRuntime> _versions = new();
    private readonly Dictionary<string, IKindRuntime> _current = new(StringComparer.Ordinal);

    public KindRegistry(IEnumerable<IKindRuntime> kinds)
    {
        foreach (var kind in kinds) Register(kind);
    }

    public void Register(IKindRuntime kind, bool current = true)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!_versions.TryAdd((kind.Id, kind.Version), kind))
            throw new ArgumentException($"Kind '{kind.Id}' version {kind.Version} is already registered.", nameof(kind));
        if (current) _current[kind.Id] = kind;
    }

    public IKindRuntime Get(string id, int version) => TryGet(id, version, out var kind)
        ? kind!
        : throw new KeyNotFoundException($"Kind '{id}' version {version} is not registered.");

    public IKindRuntime GetCurrent(string id) => TryGetCurrent(id, out var kind)
        ? kind!
        : throw new KeyNotFoundException($"No current version of kind '{id}' is registered.");

    public bool TryGet(string id, int version, out IKindRuntime? kind) => _versions.TryGetValue((id, version), out kind);
    public bool TryGetCurrent(string id, out IKindRuntime? kind) => _current.TryGetValue(id, out kind);
    public bool TryGetGenerator(string id, int version, out IGenerates? generator)
    {
        generator = TryGet(id, version, out var kind) ? kind as IGenerates : null;
        return generator is not null;
    }

    public bool TryGetCurrentGenerator(string id, out IGenerates? generator)
    {
        generator = TryGetCurrent(id, out var kind) ? kind as IGenerates : null;
        return generator is not null;
    }
}

public abstract class Kind<TPayload, TResolved, TCard, TAnswer, TStored, TReveal> : IKindRuntime
    where TPayload : class
    where TResolved : class
    where TCard : class
    where TAnswer : class
    where TStored : class
    where TReveal : class
{
    protected abstract JsonSerializerContext SerializerContext { get; }

    public abstract string Id { get; }
    public abstract int Version { get; }
    public abstract string Family { get; }
    public abstract bool Gradable { get; }

    public abstract TPayload Upgrade(int from, JsonElement payload);
    public abstract Snapshot<TCard, TReveal, TStored> UpgradeSnapshot(int from, RawSnapshot old);
    public abstract IReadOnlyList<Problem> Validate(TPayload payload, ValidateEnv env);
    public abstract TResolved Resolve(TPayload payload, ResolveContext context);
    public abstract TCard Card(Item<TPayload, TResolved> item);
    public abstract TReveal Reveal(Item<TPayload, TResolved> item);
    public abstract TStored Normalize(Item<TPayload, TResolved> item, TAnswer answer);
    public abstract Grade Grade(Item<TPayload, TResolved> item, TStored stored);

    JsonElement IKindRuntime.Upgrade(int from, JsonElement payload)
    {
        RequireObject(payload, "payload");
        return Object(Upgrade(from, payload), "payload");
    }

    JsonElement IKindRuntime.UpgradeSnapshot(int from, JsonElement snapshot)
    {
        RequireObject(snapshot, "snapshot");
        var old = RawSnapshot.From(snapshot);
        var upgraded = UpgradeSnapshot(from, old);
        var json = JsonSerializer.SerializeToElement(upgraded, typeof(Snapshot<TCard, TReveal, TStored>), SerializerContext);
        RequireObject(json, "snapshot");
        return json;
    }

    IReadOnlyList<Problem> IKindRuntime.Validate(JsonElement payload, ValidateEnv env) => Validate(Read<TPayload>(payload, "payload"), env);

    JsonElement IKindRuntime.Resolve(JsonElement payload, ResolveContext context) =>
        Object(Resolve(Read<TPayload>(payload, "payload"), context), "resolved value");

    JsonElement IKindRuntime.Card(JsonElement payload, JsonElement resolved) =>
        Object(Card(Item(payload, resolved)), "card");

    JsonElement IKindRuntime.Reveal(JsonElement payload, JsonElement resolved) =>
        Object(Reveal(Item(payload, resolved)), "reveal");

    JsonElement? IKindRuntime.Normalize(JsonElement payload, JsonElement resolved, JsonElement? answer)
    {
        if (answer is null || answer.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        try
        {
            return Object(Normalize(Item(payload, resolved), Read<TAnswer>(answer.Value, "answer")), "stored answer");
        }
        catch (JsonException exception)
        {
            throw new MalformedAnswer(exception.Message);
        }
    }

    Grade IKindRuntime.Grade(JsonElement payload, JsonElement resolved, JsonElement? stored)
    {
        if (stored is null || stored.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Gradable ? Quesshi.Shared.Grade.Of(0) : Quesshi.Shared.Grade.None;
        return CheckedGrade(Grade(Item(payload, resolved), Read<TStored>(stored.Value, "stored answer")));
    }

    private Item<TPayload, TResolved> Item(JsonElement payload, JsonElement resolved) =>
        new(Read<TPayload>(payload, "payload"), Read<TResolved>(resolved, "resolved value"));

    private T Read<T>(JsonElement json, string name) where T : class
    {
        RequireObject(json, name);
        return JsonSerializer.Deserialize(json, typeof(T), SerializerContext) as T
            ?? throw new JsonException($"The {name} is not valid for {Id} v{Version}.");
    }

    private JsonElement Object<T>(T value, string name) where T : class
    {
        var json = JsonSerializer.SerializeToElement(value, typeof(T), SerializerContext);
        RequireObject(json, name);
        return json;
    }

    private static Grade CheckedGrade(Grade grade) => grade.IsNone || (double.IsFinite(grade.Credit) && grade.Credit is >= 0 and <= 1)
        ? grade
        : throw new InvalidOperationException("Kind grade credit must be a finite value in [0, 1].");

    private static void RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException($"The {name} must be a JSON object.");
    }
}

public sealed record Item<TPayload, TResolved>(TPayload Payload, TResolved Resolved);
public sealed record Snapshot<TCard, TReveal, TStored>(TCard Card, TReveal Reveal, IReadOnlyList<TStored> Answers);
public sealed record RawSnapshot(JsonElement Card, JsonElement Reveal, IReadOnlyList<JsonElement> Answers)
{
    public static RawSnapshot From(JsonElement value)
    {
        static JsonElement Required(JsonElement source, string name) => source.TryGetProperty(name, out var property)
            ? property
            : throw new JsonException($"Snapshot is missing '{name}'.");
        var answers = Required(value, "answers").EnumerateArray().Select(answer => answer.Clone()).ToArray();
        return new(Required(value, "card").Clone(), Required(value, "reveal").Clone(), answers);
    }
}

public readonly record struct Grade(bool IsNone, double Credit)
{
    public static Grade None => new(true, 0);
    public static Grade Of(double credit) => new(false, credit);
}

public sealed record Problem(string Code, string Message);
public sealed record ValidateEnv(string Language, MediaInfo? Media = null);
public sealed record MediaInfo(string Kind, int? Width = null, int? Height = null, TimeSpan? Duration = null);
public sealed class MalformedAnswer(string message) : Exception(message);

/// <summary>Context for resolution. The RNG is SplitMix64, seeded by FNV-1a(session id) mixed with slot.</summary>
public sealed class ResolveContext(string sessionId, int slot, IReadOnlyList<RosterSeat> roster)
{
    public DeterministicRng Rng { get; } = DeterministicRng.For(sessionId, slot);
    public IReadOnlyList<RosterSeat> Roster { get; } = roster;
}

/// <summary>The roster as it stood when a session item is resolved.</summary>
public sealed record RosterSeat(int Seat, string Role, string Status, string Name);

public sealed class DeterministicRng
{
    private ulong _state;
    private DeterministicRng(ulong seed) => _state = seed;

    public static DeterministicRng For(string sessionId, int slot)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ulong hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(sessionId)) hash = (hash ^ b) * 1099511628211UL;
        return new(hash ^ ((ulong)(uint)slot * 0x9E3779B97F4A7C15UL));
    }

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public int Next(int exclusiveMaximum)
    {
        if (exclusiveMaximum <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
        return (int)(NextUInt64() % (uint)exclusiveMaximum);
    }
}
