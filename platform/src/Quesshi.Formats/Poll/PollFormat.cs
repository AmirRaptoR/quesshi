using System.Text.Json;
using Quesshi.Engine.Contracts;

namespace Quesshi.Formats.Poll;

/// <summary>Shared-pacing, unscored voting with final-only agreement results.</summary>
public sealed class PollFormat : IFormat
{
    public const string PollFamily = "poll";
    private static readonly IReadOnlySet<string> PollKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "poll_choice", "poll_roster",
    };
    private static readonly IReadOnlyDictionary<string, Type> NoActions = new Dictionary<string, Type>();

    public string Id => "poll";
    public FormatSpec Spec { get; } = new(
        Pacing.Shared,
        MinPlayers: 2,
        MaxPlayers: VotingRules.MaxParticipants,
        Lobby: true,
        Scored: false,
        Accepts: kind => kind.Family == PollFamily,
        Guests: true,
        Matchmaking: null,
        Rematch: false,
        Roles: [new Role("player")],
        Grace: TimeSpan.Zero,
        Actions: NoActions);
    public FormatVersions Versions { get; } = new(1, 1, 1);

    public JsonElement ReadRules(JsonElement raw) => raw.ValueKind == JsonValueKind.Object
        ? raw.Clone()
        : throw new JsonException("Poll rules must be an object.");

    public DrawPlan Plan(ContentSpec content, JsonElement rules)
    {
        if (content.Count <= 0) throw new ArgumentOutOfRangeException(nameof(content), "A poll needs at least one item.");
        _ = ReadRules(rules);
        return new DrawPlan(content.Count, ["poll_choice", "poll_roster"]);
    }

    public void Start(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var players = ActivePlayers(session).ToArray();
        if (players.Length is < 2 or > VotingRules.MaxParticipants)
            throw new InvalidOperationException("A poll needs between two and eight seated players.");
        if (session.Items.Count == 0) throw new InvalidOperationException("A poll needs at least one item.");
        if (session.Items.Any(item => !PollKinds.Contains(item.Kind)))
            throw new InvalidOperationException("A poll accepts only poll-family items.");

        session.State = JsonSerializer.SerializeToElement(new PollState());
        Serve(session, 0);
    }

    public void Apply(ISession session, Command command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        if (session.Session.Ended || session.Session.Position != command.At) return;

        if (command.Type == CommandType.Leave)
        {
            Leave(session, command.Player, command.At.Slot);
            return;
        }
        if (command.Type != CommandType.Answer) throw new InvalidOperationException("Poll accepts only answer and leave commands.");
        if (command.Revision < 0) throw new ArgumentOutOfRangeException(nameof(command), "Poll answer revisions cannot be negative.");
        if (!IsActivePlayer(session, command.Player)) throw new InvalidOperationException("Only active poll players can answer.");

        var prior = session.Answer(command.Player, command.At.Slot);
        if (prior is not null && command.Revision < prior.Revision) return;

        var graded = session.Grade(command);
        if (prior is not null)
        {
            if (command.Revision == prior.Revision)
            {
                if (SameAnswer(prior.Value, graded.Stored)) return;
                throw new PollRevisionConflictException();
            }

            session.Replace(graded, command.Revision);
            return;
        }

        session.Record(graded, points: 0);
        AdvanceIfReady(session, command.At.Slot);
    }

    public void Timeout(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Session.Ended || session.Session.Position is not { } position || session.Session.DueAt is not { } dueAt || session.Now < dueAt)
            return;

        foreach (var seat in ActivePlayers(session).Where(seat => session.Answer(seat.Player, position.Slot) is null).ToArray())
            session.Eliminate(seat.Player, "idle");
        AdvanceIfReady(session, position.Slot);
    }

    public object View(IViewing viewing)
    {
        ArgumentNullException.ThrowIfNull(viewing);
        var viewer = viewing.Seats.SingleOrDefault(seat => seat.Player == viewing.Viewer && seat.Role == "player");
        if (viewer is null) return new PollUnavailableView();

        if (viewing.Session.Ended) return FinishedView(viewing);
        if (viewing.Session.Position is not { } position) return new PollUnavailableView();

        var answer = viewing.Answer(viewing.Viewer, position.Slot);
        var answeredSeats = viewing.Seats
            .Where(seat => seat.Role == "player" && !seat.Eliminated && viewing.HasAnswered(seat.Player, position.Slot))
            .Select(seat => seat.Player)
            .ToArray();
        return new PollRunningView(
            viewing.Card(position.Slot),
            answer?.Value?.Clone(),
            answer?.Revision,
            answeredSeats,
            viewing.Session.DueAt);
    }

    public JsonElement UpgradeState(int from, JsonElement state) => Upgrade(from, state);
    public JsonElement UpgradeRules(int from, JsonElement rules) => Upgrade(from, rules);
    public JsonElement UpgradeResult(int from, JsonElement result) => Upgrade(from, result);

    private static void Leave(ISession session, string player, int slot)
    {
        if (!IsActivePlayer(session, player)) return;
        session.Eliminate(player, "left");
        AdvanceIfReady(session, slot);
    }

    private static void AdvanceIfReady(ISession session, int slot)
    {
        var active = ActivePlayers(session).ToArray();
        if (active.Length < 2)
        {
            End(session, EndReason.NoContest, slot);
            return;
        }
        if (active.Any(seat => session.Answer(seat.Player, slot) is null)) return;

        var nextSlot = slot + 1;
        if (nextSlot == session.Items.Count)
        {
            End(session, EndReason.Completed, nextSlot);
            return;
        }
        Serve(session, nextSlot);
    }

    private static void Serve(ISession session, int slot)
    {
        session.SetPosition(0, slot);
        session.SetDeadline(session.Session.PositionStartedAt!.Value.Add(VotingRules.IdleAfter));
    }

    private static void End(ISession session, EndReason reason, int closedSlots)
    {
        var result = VotingResults.Compute(session, closedSlots, reason == EndReason.Completed);
        var standings = session.Seats
            .Where(seat => seat.Role == "player")
            .Select(seat => new Standing(seat.Player, 1, reason == EndReason.Completed ? "completed" : "no_contest"))
            .ToArray();
        session.End(reason, standings, JsonSerializer.SerializeToElement(result));
    }

    private static PollFinishedView FinishedView(IViewing viewing)
    {
        var result = JsonSerializer.Deserialize<PollResult>(viewing.Session.Result!.Value)
            ?? throw new JsonException("Stored poll result is invalid.");
        var slots = result.Slots.Select(slot => new PollReleasedSlot(
            slot.Slot,
            viewing.Seats.Where(seat => seat.Role == "player")
                .Select(seat => new PollSubmittedAnswer(seat.Player, viewing.Answer(seat.Player, slot.Slot)?.Value?.Clone()))
                .Where(answer => answer.Value is not null)
                .ToArray(),
            slot.Counts,
            slot.AllAgreed)).ToArray();
        var seats = viewing.Seats.Where(seat => seat.Role == "player")
            .Select(seat => new PollSeatView(seat.Player, !seat.Eliminated)).ToArray();
        return new PollFinishedView(result, slots, seats);
    }

    private static IEnumerable<Seat> ActivePlayers(ISession session) => session.Seats.Where(seat => seat.Role == "player" && !seat.Eliminated);
    private static bool IsActivePlayer(ISession session, string player) => ActivePlayers(session).Any(seat => seat.Player == player);
    private static bool SameAnswer(JsonElement? left, JsonElement? right) => left is { } first && right is { } second && JsonElement.DeepEquals(first, second);
    private static JsonElement Upgrade(int from, JsonElement value) => from == 1
        ? value.Clone()
        : throw new ArgumentOutOfRangeException(nameof(from));

    private sealed record PollState;
}

/// <summary>The unchanging poll bounds and idle duration.</summary>
public static class VotingRules
{
    public const int MaxParticipants = 8;
    public static readonly TimeSpan IdleAfter = TimeSpan.FromHours(48);
}

/// <summary>Signals the standard current-view response for conflicting equal revisions.</summary>
public sealed class PollRevisionConflictException : InvalidOperationException
{
    public PollRevisionConflictException() : base("revision_conflict") { }
    public string Code => "revision_conflict";
}

public sealed record PollRunningView(
    JsonElement Card,
    JsonElement? Answer,
    int? Revision,
    IReadOnlyList<string> AnsweredSeats,
    DateTimeOffset? Deadline);

public sealed record PollUnavailableView;
public sealed record PollSubmittedAnswer(string Player, JsonElement? Value);
public sealed record PollReleasedSlot(int Slot, IReadOnlyList<PollSubmittedAnswer> Answers, IReadOnlyList<int> Counts, bool AllAgreed);
public sealed record PollSeatView(string Player, bool Active);
public sealed record PollFinishedView(PollResult Result, IReadOnlyList<PollReleasedSlot> Slots, IReadOnlyList<PollSeatView> Seats);

/// <summary>Final-only agreement result with counts aligned to each served card's option order.</summary>
public sealed record PollResult(IReadOnlyList<PollSlotResult> Slots, IReadOnlyList<PollPairStat>? PairStats, int? AllAgreedCount)
{
    public bool HasOverallStatistics => PairStats is not null;
}

public sealed record PollSlotResult(int Slot, IReadOnlyList<int> Counts, bool AllAgreed);
public sealed record PollPairStat(string FirstPlayer, string SecondPlayer, int Same, int Different, int? AgreementPercent);

public static class VotingResults
{
    public static PollResult Compute(ISession session, int closedSlots, bool completed)
    {
        ArgumentNullException.ThrowIfNull(session);
        var players = session.Seats.Where(seat => seat.Role == "player").ToArray();
        var slots = session.Items.OrderBy(item => item.Slot).Take(closedSlots)
            .Select(item => ComputeSlot(session, item, players)).ToArray();
        if (!completed) return new PollResult(slots, null, null);

        var pairs = new List<PollPairStat>();
        for (var first = 0; first < players.Length; first++)
        for (var second = first + 1; second < players.Length; second++)
        {
            var same = 0;
            var different = 0;
            foreach (var slot in slots)
            {
                var left = session.Answer(players[first].Player, slot.Slot)?.Value;
                var right = session.Answer(players[second].Player, slot.Slot)?.Value;
                if (left is null || right is null || IsNotApplicable(left.Value) || IsNotApplicable(right.Value)) continue;
                if (JsonElement.DeepEquals(left.Value, right.Value)) same++; else different++;
            }
            var compared = same + different;
            pairs.Add(new PollPairStat(players[first].Player, players[second].Player, same, different,
                compared == 0 ? null : (int)Math.Round(100d * same / compared, MidpointRounding.AwayFromZero)));
        }
        return new PollResult(slots, pairs, slots.Count(slot => slot.AllAgreed));
    }

    private static PollSlotResult ComputeSlot(ISession session, SessionItem item, IReadOnlyList<Seat> players)
    {
        var counts = new int[OptionCount(item.Kind, session.Card(item.Slot))];
        var comparable = new List<JsonElement>();
        foreach (var player in players)
        {
            var answer = session.Answer(player.Player, item.Slot)?.Value;
            if (answer is null) continue;
            var option = OptionIndex(item.Kind, session.Card(item.Slot), answer.Value);
            if (option >= 0) counts[option]++;
            if (!IsNotApplicable(answer.Value)) comparable.Add(answer.Value);
        }
        var allAgreed = comparable.Count >= 2 && comparable.All(answer => JsonElement.DeepEquals(answer, comparable[0]));
        return new PollSlotResult(item.Slot, counts, allAgreed);
    }

    private static int OptionCount(string kind, JsonElement card) => kind switch
    {
        "poll_choice" => Options(card).Count + 1,
        "poll_roster" => Options(card).Count + 3,
        _ => throw new InvalidOperationException($"'{kind}' is not a poll kind."),
    };

    private static int OptionIndex(string kind, JsonElement card, JsonElement answer)
    {
        var options = Options(card);
        if (IsNotApplicable(answer)) return kind == "poll_choice" ? options.Count : options.Count + 2;
        if (kind == "poll_choice" && answer.TryGetProperty("option", out var option))
            return options.FindIndex(value => value.TryGetProperty("id", out var id) && id.GetString() == option.GetString());
        if (kind == "poll_roster")
        {
            if (answer.TryGetProperty("seat", out var seat))
                return options.FindIndex(value => value.TryGetProperty("seat", out var current) && current.GetInt32() == seat.GetInt32());
            if (answer.TryGetProperty("several", out _)) return options.Count;
            if (answer.TryGetProperty("none", out _)) return options.Count + 1;
        }
        return -1;
    }

    private static List<JsonElement> Options(JsonElement card) => card.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
        ? options.EnumerateArray().Select(option => option.Clone()).ToList()
        : throw new JsonException("Poll card does not contain options.");

    private static bool IsNotApplicable(JsonElement answer) => answer.ValueKind == JsonValueKind.Object
        && answer.TryGetProperty("notApplicable", out var value)
        && value.ValueKind == JsonValueKind.True;
}
