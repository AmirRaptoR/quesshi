using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Engine.Contracts;
using Quesshi.Shared;

namespace Quesshi.Formats.AsyncDuel;

/// <summary>Personal, lazily timed runs that share a single creation-anchored forfeit deadline.</summary>
public sealed class AsyncDuelFormat : IFormat
{
    public const int QuestionPhase = 0;
    public const int AnsweredPhase = 1;
    public const int DonePhase = 2;

    private static readonly IReadOnlyList<string> GradableKinds = ["choice", "order", "map_pick"];
    private static readonly IReadOnlyList<Role> Roles = [new("player")];
    private static readonly IReadOnlyDictionary<string, Type> Actions = new Dictionary<string, Type>();
    private static readonly JsonSerializerOptions ResultJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Id => "async_duel";

    public FormatSpec Spec { get; } = new(
        Pacing.Personal,
        MatchRules.MinParticipants,
        MatchRules.MaxParticipants,
        Lobby: true,
        Scored: true,
        Accepts: kind => kind.Gradable,
        Guests: true,
        Matchmaking: new MatchRule(["language"], RequireOnline: false),
        Rematch: false,
        Roles,
        MatchRules.NetworkGrace,
        Actions);

    public FormatVersions Versions { get; } = new(State: 1, Rules: 1, Result: 1);

    public JsonElement ReadRules(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Async duel rules must be a JSON object.", nameof(raw));
        return raw.Clone();
    }

    public DrawPlan Plan(ContentSpec content, JsonElement rules)
    {
        _ = ReadRules(rules);
        if (!MatchRules.IsValidCount(content.Count))
            throw new ArgumentOutOfRangeException(nameof(content), content.Count, "The question count is not supported by async duels.");
        return new DrawPlan(content.Count, GradableKinds);
    }

    public void Start(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Session.Ended) return;
        session.SetDeadline(CreatedAt(session.Session) + MatchRules.ForfeitAfter);
    }

    public DateTimeOffset? LobbyDeadline(SessionRow session) => CreatedAt(session) + MatchRules.ForfeitAfter;

    public void Apply(ISession session, Command command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        if (session.Session.Ended) return;

        var seat = session.Seats.SingleOrDefault(candidate => candidate.Player == command.Player);
        if (seat is null || seat.Role != "player" || seat.Status != SeatStatus.Active) return;

        if (seat.Position is null)
        {
            if (command.Type == CommandType.Next) session.Serve(seat.Player, 0);
            return;
        }

        if (seat.Position.Phase == QuestionPhase)
        {
            if (Expired(seat, command.Received))
            {
                session.RecordMiss(seat.Player, seat.Position.Slot);
                session.ReleaseTo(seat.Player, seat.Position.Slot);
                session.SetSeatPosition(seat.Player, AnsweredPhase, seat.Position.Slot);
                return;
            }

            if (command.Type != CommandType.Answer || command.At != seat.Position) return;
            var graded = session.Grade(command);
            if (graded.Duplicate) return;

            var item = session.Items.Single(item => item.Slot == seat.Position.Slot);
            session.Record(graded, Scoring.Score(graded.Credit, graded.Taken, MatchRules.QuestionTime, item.Level));
            session.ReleaseTo(seat.Player, seat.Position.Slot);
            session.SetSeatPosition(seat.Player, AnsweredPhase, seat.Position.Slot);
            return;
        }

        if (seat.Position.Phase != AnsweredPhase || command.Type != CommandType.Next || command.At != seat.Position) return;

        var nextSlot = seat.Position.Slot + 1;
        if (nextSlot < session.ItemCount)
        {
            session.Serve(seat.Player, nextSlot);
            return;
        }

        FinishSeat(session, seat);
        CompleteWhenEveryPlayerIsDone(session);
    }

    public void Timeout(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Session.Ended) return;

        foreach (var seat in PlayerSeats(session).Where(seat => seat.Status != SeatStatus.Done))
            session.SetSeatResult(seat.Player, SeatStatus.Left, SeatResult(session, seat, forfeited: true));

        var standings = Standings(session);
        session.End(EndReason.Forfeit, standings, Result(standings));
    }

    public object View(IViewing viewing)
    {
        ArgumentNullException.ThrowIfNull(viewing);
        var viewer = viewing.Seats.SingleOrDefault(seat => seat.Player == viewing.Viewer);
        if (viewer is null) return new AsyncDuelView(null, []);

        var maySeeOthers = viewing.Session.Ended || viewer.Status == SeatStatus.Done;
        var you = Run(viewing, viewer, includePrivateContent: true, includeAnswers: true);
        var others = maySeeOthers
            ? PlayerSeats(viewing).Where(seat => seat.Player != viewer.Player)
                .Select(seat => Run(viewing, seat, includePrivateContent: false, includeAnswers: true)).ToArray()
            : [];
        return new AsyncDuelView(you, others);
    }

    public DateTimeOffset? Deadline(IViewing viewing)
    {
        ArgumentNullException.ThrowIfNull(viewing);
        var viewer = viewing.Seats.SingleOrDefault(seat => seat.Player == viewing.Viewer);
        return viewer?.Position is { Phase: QuestionPhase } && viewer.PositionStartedAt is { } startedAt
            ? startedAt + MatchRules.QuestionTime
            : viewing.Session.DueAt;
    }

    public JsonElement UpgradeState(int from, JsonElement state) => Upgrade(from, state, nameof(state));
    public JsonElement UpgradeRules(int from, JsonElement rules) => Upgrade(from, rules, nameof(rules));
    public JsonElement UpgradeResult(int from, JsonElement result) => Upgrade(from, result, nameof(result));

    private static bool Expired(Seat seat, DateTimeOffset received) => seat.PositionStartedAt is { } served
        && received > served + MatchRules.QuestionTime + MatchRules.NetworkGrace;

    private static DateTimeOffset CreatedAt(SessionRow session) => session.CreatedAt != default
        ? session.CreatedAt
        : session.StartedAt ?? DateTimeOffset.UnixEpoch;

    private static void FinishSeat(ISession session, Seat seat)
    {
        var position = seat.Position!;
        session.SetSeatPosition(seat.Player, DonePhase, position.Slot);
        session.SetSeatResult(seat.Player, SeatStatus.Done, SeatResult(session, seat, forfeited: false));
    }

    private static void CompleteWhenEveryPlayerIsDone(ISession session)
    {
        var players = PlayerSeats(session);
        if (players.Count < MatchRules.MinParticipants || players.Any(player => player.Status != SeatStatus.Done)) return;

        var standings = Standings(session);
        session.End(EndReason.Completed, standings, Result(standings));
    }

    private static IReadOnlyList<Standing> Standings(ISession session)
    {
        var ranked = PlayerSeats(session)
            .OrderByDescending(seat => seat.Score)
            .ThenBy(seat => seat.Player, StringComparer.Ordinal)
            .ToArray();
        var places = new int[ranked.Length];
        for (var index = 0; index < ranked.Length; index++)
            places[index] = index > 0 && ranked[index].Score == ranked[index - 1].Score ? places[index - 1] : index + 1;

        var tiedFirst = places.Count(place => place == 1) > 1;
        return ranked.Select((seat, index) => new Standing(
            seat.Player,
            places[index],
            places[index] == 1 ? tiedFirst ? "draw" : "win" : "loss")).ToArray();
    }

    private static JsonElement SeatResult(ISession session, Seat seat, bool forfeited)
    {
        var answers = session.Answers(seat.Player);
        return Json(new AsyncDuelSeatResult(
            answers.Count(answer => !answer.Miss),
            RunBegan: seat.Position is not null || answers.Count > 0,
            Forfeited: forfeited));
    }

    private static JsonElement Result(IReadOnlyList<Standing> standings) => Json(new AsyncDuelResult(standings));

    private static AsyncDuelRunView Run(IViewing viewing, Seat seat, bool includePrivateContent, bool includeAnswers)
    {
        var position = seat.Position;
        var slot = position?.Slot;
        var served = includePrivateContent && slot is { } value && value >= 0 && value < viewing.ItemCount;
        var revealed = served && seat.ReleasedSlot >= slot;
        return new AsyncDuelRunView(
            seat.Player,
            position,
            seat.Score,
            seat.Status,
            seat.Place,
            seat.Outcome,
            served ? viewing.Card(slot!.Value) : null,
            revealed ? viewing.Reveal(slot!.Value) : null,
            includeAnswers ? viewing.Answers(seat.Player) : []);
    }

    private static IReadOnlyList<Seat> PlayerSeats(ISession session) => session.Seats.Where(seat => seat.Role == "player").ToArray();
    private static IReadOnlyList<Seat> PlayerSeats(IViewing viewing) => viewing.Seats.Where(seat => seat.Role == "player").ToArray();

    private static JsonElement Upgrade(int from, JsonElement value, string name)
    {
        if (from != 1) throw new ArgumentOutOfRangeException(nameof(from), from, $"Async duel {name} version is not supported.");
        return value.Clone();
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, ResultJson);
}

public sealed record AsyncDuelSeatResult(int Answered, bool RunBegan, bool Forfeited);
public sealed record AsyncDuelResult(IReadOnlyList<Standing> Standings);
public sealed record AsyncDuelRunView(
    string Player,
    Position? Position,
    int Score,
    SeatStatus Status,
    int? Place,
    string? Outcome,
    JsonElement? Card,
    JsonElement? Reveal,
    IReadOnlyList<AnswerRecord> Answers);
public sealed record AsyncDuelView(AsyncDuelRunView? You, IReadOnlyList<AsyncDuelRunView> Others);
