using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Engine.Contracts;
using Quesshi.Shared;

namespace Quesshi.Formats.Live;

/// <summary>Shared, paced duels in which every player sees the same question at the same time.</summary>
public sealed class LiveFormat : IFormat
{
    public const int CountdownPhase = 0;
    public const int QuestionPhase = 1;
    public const int RevealPhase = 2;

    /// <summary>See the selected live maximum in docs/platform/benchmark.md.</summary>
    public const int MeasuredMaxPlayers = 2;

    private static readonly IReadOnlyList<string> GradableKinds = ["choice", "order", "map_pick"];
    private static readonly IReadOnlyList<Role> Roles = [new("player")];
    private static readonly IReadOnlyDictionary<string, Type> Actions = new Dictionary<string, Type>();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan Countdown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Reveal = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumLateness = TimeSpan.FromSeconds(40);

    public string Id => "live";

    public FormatSpec Spec { get; } = new(
        Pacing.Shared,
        MatchRules.MinParticipants,
        MeasuredMaxPlayers,
        Lobby: true,
        Scored: true,
        Accepts: kind => kind.Gradable,
        Guests: true,
        Matchmaking: new MatchRule(["language", "questionCount"], RequireOnline: true),
        Rematch: true,
        Roles,
        MatchRules.NetworkGrace,
        Actions);

    public FormatVersions Versions { get; } = new(State: 1, Rules: 1, Result: 1);
    public TimeSpan? MaximumDeadlineLateness => MaximumLateness;

    public JsonElement ReadRules(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Live rules must be a JSON object.", nameof(raw));
        return raw.Clone();
    }

    public DrawPlan Plan(ContentSpec content, JsonElement rules)
    {
        _ = ReadRules(rules);
        if (!MatchRules.IsValidCount(content.Count))
            throw new ArgumentOutOfRangeException(nameof(content), content.Count, "The question count is not supported by live duels.");
        return new DrawPlan(content.Count, GradableKinds);
    }

    public void Start(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Session.Ended) return;

        session.State = Json(new LiveState(null, [], [], new Dictionary<string, int>(StringComparer.Ordinal)));
        session.SetPosition(CountdownPhase, 0);
        session.SetDeadline(session.Now + Countdown);
    }

    public void Apply(ISession session, Command command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        if (session.Session.Ended) return;

        var seat = PlayerSeats(session).SingleOrDefault(candidate => candidate.Player == command.Player);
        if (seat is null || seat.Status != SeatStatus.Active) return;

        if (command.Type == CommandType.Leave)
        {
            Leave(session, seat);
            return;
        }

        if (session.Session.Position is not { Phase: QuestionPhase } position
            || command.Type != CommandType.Answer || command.At != position
            || session.Session.DueAt is { } dueAt && command.Received > dueAt)
            return;

        var state = State(session);
        if (!state.Expected.Contains(seat.Player, StringComparer.Ordinal)) return;
        var graded = session.Grade(command);
        if (graded.Duplicate) return;
        var item = session.Items.Single(item => item.Slot == position.Slot);
        session.Record(graded, Scoring.Score(graded.Credit, graded.Taken, MatchRules.QuestionTime, item.Level));

        var answered = state.Answered.Append(seat.Player).Distinct(StringComparer.Ordinal).ToArray();
        var streaks = Copy(state.MissStreaks);
        streaks[seat.Player] = 0;
        state = state with { Answered = answered, MissStreaks = streaks };
        session.State = Json(state);
        if (state.Expected.All(player => state.Answered.Contains(player, StringComparer.Ordinal))) CloseRound(session, state);
    }

    public void Timeout(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Session.Ended || session.Session.Position is null) return;

        switch (session.Session.Position.Phase)
        {
            case CountdownPhase:
                OpenQuestion(session, session.Session.DueAt ?? session.Now, 0, State(session));
                break;
            case QuestionPhase:
                CloseRound(session, State(session));
                break;
            case RevealPhase:
                var slot = session.Session.Position.Slot + 1;
                if (slot >= session.ItemCount) EndCompleted(session, State(session));
                else OpenQuestion(session, session.Session.DueAt ?? session.Now, slot, State(session));
                break;
        }
    }

    public object View(IViewing viewing)
    {
        ArgumentNullException.ThrowIfNull(viewing);
        var position = viewing.Session.Position;
        var state = State(viewing.State);
        var card = position is { Phase: QuestionPhase or RevealPhase } && position.Slot < viewing.ItemCount
            ? viewing.Card(position.Slot) : (JsonElement?)null;
        var reveal = position is { Phase: RevealPhase } && position.Slot < viewing.ItemCount
            ? viewing.Reveal(position.Slot) : null;
        return new LiveView(position, state.Expected, state.Answered, card, reveal);
    }

    public DateTimeOffset? Deadline(IViewing viewing)
    {
        ArgumentNullException.ThrowIfNull(viewing);
        var state = State(viewing.State);
        return viewing.Session.Position is { Phase: QuestionPhase } && state.QuestionEndsAt is { } questionEndsAt
            ? questionEndsAt
            : viewing.Session.DueAt;
    }

    public JsonElement UpgradeState(int from, JsonElement state) => Upgrade(from, state, nameof(state));
    public JsonElement UpgradeRules(int from, JsonElement rules) => Upgrade(from, rules, nameof(rules));
    public JsonElement UpgradeResult(int from, JsonElement result) => Upgrade(from, result, nameof(result));

    private static void OpenQuestion(ISession session, DateTimeOffset startsAt, int slot, LiveState state)
    {
        var expected = PlayerSeats(session).Where(seat => seat.Status == SeatStatus.Active).Select(seat => seat.Player).ToArray();
        if (expected.Length == 0)
        {
            EndNoContest(session);
            return;
        }
        if (expected.Length == 1)
        {
            EndCompleted(session, state);
            return;
        }

        var endsAt = startsAt + MatchRules.QuestionTime;
        session.SetPosition(QuestionPhase, slot);
        session.State = Json(state with { QuestionEndsAt = endsAt, Expected = expected, Answered = [] });
        session.SetDeadline(endsAt + MatchRules.NetworkGrace);
    }

    private static void CloseRound(ISession session, LiveState state)
    {
        if (session.Session.Position is not { Phase: QuestionPhase } position) return;

        var streaks = Copy(state.MissStreaks);
        foreach (var player in state.Expected.Where(player => !state.Answered.Contains(player, StringComparer.Ordinal)))
        {
            session.RecordMiss(player, position.Slot);
            var misses = streaks.TryGetValue(player, out var current) ? current + 1 : 1;
            streaks[player] = misses;
            if (misses == 3)
            {
                session.Eliminate(player, "missed_rounds");
                session.SetSeatResult(player, SeatStatus.Eliminated, Json(new LiveSeatResult(true, position.Slot, 0)));
            }
        }

        session.Release(position.Slot);
        var after = state with { MissStreaks = streaks };
        if (ActiveCount(session) == 0)
        {
            EndNoContest(session);
            return;
        }
        if (ActiveCount(session) == 1)
        {
            EndCompleted(session, after);
            return;
        }

        var questionEndsAt = state.QuestionEndsAt ?? session.Session.DueAt!.Value - MatchRules.NetworkGrace;
        session.SetPosition(RevealPhase, position.Slot);
        session.State = Json(after with { QuestionEndsAt = null, Expected = [], Answered = [] });
        session.SetDeadline(questionEndsAt + Reveal);
    }

    private static void Leave(ISession session, Seat seat)
    {
        var state = State(session);
        var exitRound = session.Session.Position?.Slot ?? 0;
        session.SetSeatResult(seat.Player, SeatStatus.Left, Json(new LiveSeatResult(true, exitRound, 0)));

        if (session.Session.Position is { Phase: QuestionPhase } && state.Expected.Contains(seat.Player, StringComparer.Ordinal)
            && !state.Answered.Contains(seat.Player, StringComparer.Ordinal))
        {
            state = state with { Expected = state.Expected.Where(player => player != seat.Player).ToArray() };
            session.State = Json(state);
        }

        if (ActiveCount(session) == 0) EndNoContest(session);
        else if (ActiveCount(session) == 1) EndCompleted(session, state);
        else if (session.Session.Position is { Phase: QuestionPhase } && state.Expected.All(player => state.Answered.Contains(player, StringComparer.Ordinal))) CloseRound(session, state);
    }

    private static int ActiveCount(ISession session) => PlayerSeats(session).Count(seat => seat.Status == SeatStatus.Active);
    private static IReadOnlyList<Seat> PlayerSeats(ISession session) => session.Seats.Where(seat => seat.Role == "player").ToArray();

    private static void EndCompleted(ISession session, LiveState state)
    {
        var standings = Standings(session, state);
        foreach (var seat in PlayerSeats(session).Where(seat => seat.Status == SeatStatus.Active))
            session.SetSeatResult(seat.Player, SeatStatus.Done, Json(new LiveSeatResult(false, null, seat.Score)));
        session.End(EndReason.Completed, standings.Select(standing => new Standing(standing.Player, standing.Place, standing.Outcome)).ToArray(), Json(new LiveResult(standings)));
    }

    private static void EndNoContest(ISession session)
    {
        var standings = PlayerSeats(session).Select(seat => new LiveStanding(seat.Player, 0, 0, "none")).ToArray();
        session.End(EndReason.NoContest, standings.Select(standing => new Standing(standing.Player, standing.Place, standing.Outcome)).ToArray(), Json(new LiveResult(standings)));
    }

    private static IReadOnlyList<LiveStanding> Standings(ISession session, LiveState state)
    {
        var ranked = PlayerSeats(session)
            .Select(seat => new RankedSeat(seat, seat.Status is SeatStatus.Left or SeatStatus.Eliminated, ExitRound(seat)))
            .OrderBy(entry => entry.Abandoned)
            .ThenByDescending(entry => entry.Abandoned ? entry.ExitRound : entry.Seat.Score)
            .ThenBy(entry => entry.Seat.Player, StringComparer.Ordinal)
            .ToArray();
        var places = new int[ranked.Length];
        for (var index = 0; index < ranked.Length; index++)
        {
            var tied = index > 0 && ranked[index].Abandoned == ranked[index - 1].Abandoned
                && (ranked[index].Abandoned ? ranked[index].ExitRound == ranked[index - 1].ExitRound : ranked[index].Seat.Score == ranked[index - 1].Seat.Score);
            places[index] = tied ? places[index - 1] : index + 1;
        }
        var tiedFirst = places.Count(place => place == 1) > 1;
        return ranked.Select((entry, index) => new LiveStanding(
            entry.Seat.Player,
            entry.Abandoned ? 0 : entry.Seat.Score,
            places[index],
            places[index] == 1 ? tiedFirst ? "draw" : "win" : "loss")).ToArray();
    }

    private static int ExitRound(Seat seat)
    {
        if (seat.Result is not { } result) return -1;
        return JsonSerializer.Deserialize<LiveSeatResult>(result.GetRawText(), JsonOptions)?.ExitRound ?? -1;
    }

    private static LiveState State(ISession session) => State(session.State);
    private static LiveState State(JsonElement state) => JsonSerializer.Deserialize<LiveState>(state.GetRawText(), JsonOptions)
        ?? new LiveState(null, [], [], new Dictionary<string, int>(StringComparer.Ordinal));
    private static Dictionary<string, int> Copy(IReadOnlyDictionary<string, int> values) => values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    private static JsonElement Upgrade(int from, JsonElement value, string parameter) => from == 1 ? value.Clone() : throw new ArgumentOutOfRangeException(parameter, from, "Unknown live format version.");

    private sealed record LiveState(DateTimeOffset? QuestionEndsAt, IReadOnlyList<string> Expected, IReadOnlyList<string> Answered, IReadOnlyDictionary<string, int> MissStreaks);
    private sealed record RankedSeat(Seat Seat, bool Abandoned, int ExitRound);
}

public sealed record LiveSeatResult(bool Abandoned, int? ExitRound, int StandingScore);
public sealed record LiveStanding(string Player, int Score, int Place, string Outcome);
public sealed record LiveResult(IReadOnlyList<LiveStanding> Standings);
public sealed record LiveView(Position? Position, IReadOnlyList<string> Expected, IReadOnlyList<string> Answered, JsonElement? Card, JsonElement? Reveal);
