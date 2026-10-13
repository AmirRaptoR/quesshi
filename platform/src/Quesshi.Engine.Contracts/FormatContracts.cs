using System.Text.Json;
using Quesshi.Shared;

namespace Quesshi.Engine.Contracts;

public enum Pacing { Shared, Personal }
public enum CommandType { Answer, Next, Leave, Action }

/// <summary>A format rejected a well-formed command with a stable client-visible code.</summary>
public class SessionCommandException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
public enum EndReason { Completed, Forfeit, NoContest }
public enum SeatStatus { Active, Done, Left, Eliminated }

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
    DateTimeOffset? Deadline(IViewing viewing) => viewing.Session.DueAt;
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
    JsonElement? Result = null,
    DateTimeOffset? PositionStartedAt = null,
    DateTimeOffset CreatedAt = default,
    string? Format = null,
    long Version = 0,
    DateTimeOffset? RetryAt = null,
    string Status = "running",
    string? Code = null,
    JsonElement? Content = null,
    JsonElement? Rules = null,
    long ConfigVersion = 0);

public sealed record Seat(
    string Player,
    string Role,
    Position? Position = null,
    int Score = 0,
    bool Eliminated = false,
    int? ReleasedSlot = null,
    DateTimeOffset? PositionStartedAt = null,
    int? Place = null,
    string? Outcome = null,
    SeatStatus Status = SeatStatus.Active,
    JsonElement? Result = null,
    int Number = 0,
    string? DisplayName = null);

/// <summary>Item metadata deliberately excludes question payload and resolved answer data.</summary>
public sealed record SessionItem(int Slot, string Kind, int KindVersion, int? Level, string? Category);

/// <summary>The authoritative answer or miss stored for one player's served slot.</summary>
public sealed record AnswerRecord(
    JsonElement? Stored,
    bool IsGraded,
    double Credit,
    int Points,
    TimeSpan Taken,
    DateTimeOffset Received,
    bool Miss = false,
    int Revision = 0,
    string? Hash = null);

public sealed record Graded(
    Command Command,
    JsonElement? Stored,
    bool IsGraded,
    double Credit,
    TimeSpan Taken,
    bool OnTime,
    bool Duplicate);

/// <summary>A format-safe projection of one persisted answer row.</summary>
public sealed record StoredAnswer(JsonElement? Value, int Revision, bool Miss = false);

public interface ISession
{
    SessionRow Session { get; }
    JsonElement State { get; set; }
    IReadOnlyList<Seat> Seats { get; }
    IReadOnlyList<SessionItem> Items { get; }
    int ItemCount { get; }
    DateTimeOffset Now { get; }
    StoredAnswer? Answer(string player, int slot);
    JsonElement Card(int slot);
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
    void SetSeatResult(string player, SeatStatus status, JsonElement result);
    IReadOnlyList<AnswerRecord> Answers(string player);
    void End(EndReason reason, IReadOnlyList<Standing> standings, JsonElement result);
}

public interface IViewing
{
    SessionRow Session { get; }
    JsonElement State { get; }
    IReadOnlyList<Seat> Seats { get; }
    IReadOnlyList<SessionItem> Items { get; }
    int ItemCount { get; }
    string Viewer { get; }
    /// <summary>The stored answer of the viewer, or of any player once the session has ended; otherwise null.</summary>
    StoredAnswer? Answer(string player, int slot);
    /// <summary>Whether the player has an answer for the slot, without revealing it.</summary>
    bool HasAnswered(string player, int slot);
    JsonElement Card(int slot);
    JsonElement? Reveal(int slot);
    IReadOnlyList<AnswerRecord> Answers(string player);
}
