using System.Text.Json;
using Quesshi.Engine.Contracts;

namespace Quesshi.Formats.Flashcards;

/// <summary>Solo, unscored, personally paced card review that is created already running.</summary>
public sealed class FlashcardsFormat : IFormat
{
    public const string CardFamily = "card";
    public const string CardKind = "flashcard";
    public const string FlipAction = "flip";
    public const int FrontPhase = 0;
    public const int BackPhase = 1;
    public const int Ratings = 4;
    public const int MaxRating = Ratings - 1;
    public static readonly TimeSpan IdleAfter = TimeSpan.FromDays(30);

    private static readonly IReadOnlyDictionary<string, Type> FormatActions = new Dictionary<string, Type> { [FlipAction] = typeof(FlipPayload) };
    private static readonly JsonSerializerOptions ResultJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Id => "flashcards";

    public FormatSpec Spec { get; } = new(
        Pacing.Personal,
        MinPlayers: 1,
        MaxPlayers: 1,
        Lobby: false,
        Scored: false,
        Accepts: kind => kind.Family == CardFamily,
        Guests: true,
        Matchmaking: null,
        Rematch: false,
        Roles: [new Role("player")],
        Grace: TimeSpan.Zero,
        Actions: FormatActions);

    public FormatVersions Versions { get; } = new(State: 1, Rules: 1, Result: 1);

    public JsonElement ReadRules(JsonElement raw) => raw.ValueKind == JsonValueKind.Object
        ? raw.Clone()
        : throw new JsonException("Flashcards rules must be an object.");

    public DrawPlan Plan(ContentSpec content, JsonElement rules)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Count <= 0) throw new ArgumentOutOfRangeException(nameof(content), "A deck needs at least one card.");
        _ = ReadRules(rules);
        return new DrawPlan(content.Count, [CardKind]);
    }

    public void Start(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Session.Ended) return;
        var player = PlayerSeat(session) ?? throw new InvalidOperationException("Flashcards need exactly one player seat.");
        if (session.ItemCount == 0) throw new InvalidOperationException("Flashcards need at least one card.");

        session.Serve(player.Player, 0);
        session.SetDeadline(session.Now + IdleAfter);
    }

    public void Apply(ISession session, Command command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        if (session.Session.Ended) return;

        var seat = PlayerSeat(session);
        if (seat is null || seat.Player != command.Player || seat.Position is not { } position) return;
        // A repeat of an earlier step: the first one already moved the card on.
        if (position != command.At) return;

        switch (command.Type)
        {
            case CommandType.Action when command.Name == FlipAction:
                if (position.Phase != FrontPhase) return;
                session.ReleaseTo(seat.Player, position.Slot);
                session.SetSeatPosition(seat.Player, BackPhase, position.Slot);
                session.SetDeadline(session.Now + IdleAfter);
                return;
            case CommandType.Answer:
                if (position.Phase != BackPhase) throw new SessionCommandException("stale_position");
                Rate(session, seat, position, command);
                return;
            default:
                throw new SessionCommandException("invalid_command");
        }
    }

    public void Timeout(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Session.Ended || session.Session.DueAt is not { } dueAt || session.Now < dueAt) return;
        Finish(session);
    }

    public object View(IViewing viewing)
    {
        ArgumentNullException.ThrowIfNull(viewing);
        var seat = viewing.Seats.SingleOrDefault(candidate => candidate.Role == "player" && candidate.Player == viewing.Viewer);
        if (seat is null) return new FlashcardsUnavailableView();

        if (viewing.Session.Ended)
        {
            var result = viewing.Session.Result is { } stored
                ? JsonSerializer.Deserialize<FlashcardsResult>(stored, ResultJson)
                : null;
            return new FlashcardsFinishedView(result?.Counts ?? new int[Ratings], viewing.ItemCount);
        }
        if (seat.Position is not { } position) return new FlashcardsUnavailableView();

        var back = position.Phase == BackPhase && seat.ReleasedSlot >= position.Slot;
        var item = viewing.Items.SingleOrDefault(candidate => candidate.Slot == position.Slot);
        return new FlashcardsRunningView(
            position.Slot,
            position.Phase,
            back ? "back" : "front",
            viewing.Card(position.Slot),
            back ? viewing.Reveal(position.Slot) : null,
            viewing.ItemCount,
            viewing.Answers(seat.Player).Count(answer => !answer.Miss),
            viewing.Session.DueAt,
            item?.Kind,
            item?.KindVersion,
            item?.Category);
    }

    public JsonElement UpgradeState(int from, JsonElement state) => Upgrade(from, state);
    public JsonElement UpgradeRules(int from, JsonElement rules) => Upgrade(from, rules);
    public JsonElement UpgradeResult(int from, JsonElement result) => Upgrade(from, result);

    private static void Rate(ISession session, Seat seat, Position position, Command command)
    {
        if (session.Answer(seat.Player, position.Slot) is not null) return;

        session.Record(session.Grade(command), points: 0);
        var next = position.Slot + 1;
        if (next < session.ItemCount)
        {
            session.Serve(seat.Player, next);
            session.SetDeadline(session.Now + IdleAfter);
            return;
        }
        Finish(session);
    }

    private static void Finish(ISession session)
    {
        var player = PlayerSeat(session)!;
        var counts = new int[Ratings];
        foreach (var answer in session.Answers(player.Player))
            if (!answer.Miss && RatingOf(answer.Stored) is { } rating) counts[rating]++;
        session.End(
            EndReason.Completed,
            [new Standing(player.Player, 1, "none")],
            JsonSerializer.SerializeToElement(new FlashcardsResult(counts), ResultJson));
    }

    /// <summary>The kind stores a rating answer as an object holding an integer 0..3 under <c>rating</c>.</summary>
    private static int? RatingOf(JsonElement? stored) =>
        stored is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty("rating", out var rating)
        && rating.ValueKind == JsonValueKind.Number
        && rating.TryGetInt32(out var number)
        && number is >= 0 and <= MaxRating
            ? number
            : null;

    private static Seat? PlayerSeat(ISession session) => session.Seats.SingleOrDefault(seat => seat.Role == "player");

    private static JsonElement Upgrade(int from, JsonElement value) => from == 1
        ? value.Clone()
        : throw new ArgumentOutOfRangeException(nameof(from));
}

/// <summary>The flip action carries no data.</summary>
public sealed record FlipPayload;

/// <summary>Finished counts, indexed by rating 0..3.</summary>
public sealed record FlashcardsResult(int[] Counts);

public sealed record FlashcardsUnavailableView;

/// <summary>The current card. <c>Back</c> is null until the player flips; <c>Side</c> is <c>front</c> or <c>back</c>; commands address <c>Phase</c> (0 front, 1 back) and <c>Slot</c>.</summary>
public sealed record FlashcardsRunningView(
    int Slot,
    int Phase,
    string Side,
    JsonElement Card,
    JsonElement? Back,
    int Total,
    int Rated,
    DateTimeOffset? Deadline,
    string? Kind = null,
    int? KindVersion = null,
    string? Category = null);

public sealed record FlashcardsFinishedView(int[] Counts, int Total);
