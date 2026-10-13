using System.Text.Json;
using Quesshi.Shared;

namespace Quesshi.Engine.Contracts;

public enum Pacing { Shared, Personal }
public enum CommandType { Answer, Next, Leave, Action }
public enum EndReason { Completed, Forfeit, NoContest }

public sealed record Role(string Id, bool Plays = true);
public sealed record MatchRule(IReadOnlyList<string> Settings, bool RequireOnline);
public sealed record FormatVersions(int State, int Rules, int Result);
public sealed record Position(int Phase, int Slot, int Step = 0);
public sealed record ContentSpec(string Source, JsonElement Params, int Count);
public sealed record DrawPlan(int Count, IReadOnlyList<string> AcceptedKinds, IReadOnlyList<int>? Levels = null);
public sealed record Standing(string Player, int Place, string? Outcome = null);

public sealed record FormatSpec(
    Pacing Pacing,
    int MinPlayers,
    int MaxPlayers,
    bool Lobby,
    bool Scored,
    Func<IKind, bool> Accepts,
    bool Guests,
    MatchRule? Matchmaking,
    bool Rematch,
    IReadOnlyList<Role> Roles,
    TimeSpan Grace,
    IReadOnlyDictionary<string, Type> Actions);

public sealed record Command(
    CommandType Type,
    string Player,
    Position At,
    int Revision,
    string? Name,
    JsonElement? Payload,
    DateTimeOffset Received);

/// <summary>A format is pure: it changes only the session transition supplied by the engine.</summary>
public interface IFormat
{
    string Id { get; }
    FormatSpec Spec { get; }
    FormatVersions Versions { get; }
    JsonElement ReadRules(JsonElement raw);
    DrawPlan Plan(ContentSpec content, JsonElement rules);
    void Start(ISession session);
    void Apply(ISession session, Command command);
    void Timeout(ISession session);
    object View(IViewing viewing);
    JsonElement UpgradeState(int from, JsonElement state);
    JsonElement UpgradeRules(int from, JsonElement rules);
    JsonElement UpgradeResult(int from, JsonElement result);
}

public interface IFormatRegistry
{
    IFormat Get(string id);
    bool TryGet(string id, out IFormat? format);
}

/// <summary>Only formats deliberately supplied to this registry can be resolved.</summary>
public sealed class FormatRegistry : IFormatRegistry
{
    private readonly IReadOnlyDictionary<string, IFormat> _formats;

    public FormatRegistry(IEnumerable<IFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var registered = new Dictionary<string, IFormat>(StringComparer.Ordinal);
        foreach (var format in formats)
        {
            ArgumentNullException.ThrowIfNull(format);
            if (!registered.TryAdd(format.Id, format))
                throw new ArgumentException($"Format '{format.Id}' is already registered.", nameof(formats));
        }
        _formats = registered;
    }

    public IFormat Get(string id) => TryGet(id, out var format)
        ? format!
        : throw new KeyNotFoundException($"Format '{id}' is not registered.");

    public bool TryGet(string id, out IFormat? format) => _formats.TryGetValue(id, out format);
}

public sealed record SessionRow(
    string Id,
    DateTimeOffset? StartedAt,
    DateTimeOffset? DueAt,
    Position? Position,
    bool Ended = false,
    EndReason? EndReason = null,
    JsonElement? Result = null);

public sealed record Seat(
    string Player,
    string Role,
    Position? Position = null,
    int Score = 0,
    bool Eliminated = false,
    int? ReleasedSlot = null);

/// <summary>Item metadata deliberately excludes question payload and resolved answer data.</summary>
public sealed record SessionItem(int Slot, string Kind, int KindVersion, int? Level, string? Category);

public sealed record Graded(
    Command Command,
    JsonElement? Stored,
    bool IsGraded,
    double Credit,
    TimeSpan Taken,
    bool OnTime,
    bool Duplicate);

public interface ISession
{
    SessionRow Session { get; }
    JsonElement State { get; set; }
    IReadOnlyList<Seat> Seats { get; }
    IReadOnlyList<SessionItem> Items { get; }
    Graded Grade(Command command);
    void Record(Graded graded, int points);
    void Replace(Graded graded, int revision);
    void RecordMiss(string player, int slot);
    void RecordMisses(int slot);
    void SetPosition(int phase, int slot, int step = 0);
    void SetSeatPosition(string player, int phase, int slot, int step = 0);
    void Serve(string player, int slot);
    void Release(int slot);
    void ReleaseTo(string player, int slot);
    void SetDeadline(DateTimeOffset at);
    void ClearDeadline();
    void Eliminate(string player, string why);
    void End(EndReason reason, IReadOnlyList<Standing> standings, JsonElement result);
}

public interface IViewing
{
    SessionRow Session { get; }
    JsonElement State { get; }
    IReadOnlyList<Seat> Seats { get; }
    IReadOnlyList<SessionItem> Items { get; }
    string Viewer { get; }
    JsonElement Card(int slot);
    JsonElement? Reveal(int slot);
}
