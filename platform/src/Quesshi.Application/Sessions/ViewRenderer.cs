using System.Text.Json;
using Quesshi.Engine.Contracts;
using Quesshi.Shared;

namespace Quesshi.Application.Sessions;

public sealed record RenderedSessionView(long Version, string Json);

/// <summary>Immutable rendering input whose shared JSON is produced once for every session version.</summary>
public sealed class SessionViewSnapshot
{
    internal SessionViewSnapshot(SessionState state, DateTimeOffset now)
    {
        State = state;
        Now = now;
        Shared = JsonSerializer.SerializeToElement(new SharedSession(
            state.Session.Id,
            state.Session.Code ?? "",
            state.Session.Format ?? "",
            state.Session.Status,
            state.Session.Position is { } position ? position.Phase.ToString(System.Globalization.CultureInfo.InvariantCulture) : "idle",
            state.Session.Position?.Slot ?? -1,
            state.Session.Position?.Step ?? 0,
            state.Session.Version,
            state.Session.ConfigVersion,
            now,
            (state.Session.Content ?? Empty).Clone(),
            (state.Session.Rules ?? Empty).Clone()));
    }

    internal SessionState State { get; }
    internal DateTimeOffset Now { get; }
    /// <summary>The common wire fields serialized once for this immutable version.</summary>
    internal JsonElement Shared { get; }
    private sealed record SharedSession(string Id, string Code, string Format, string Status, string Phase, int Slot, int Step,
        long Version, long ConfigVersion, DateTimeOffset Now, JsonElement Content, JsonElement Rules);
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();
}

/// <summary>Shared command and stream view boundary. It owns no persistence or transition state.</summary>
public interface IViewRenderer
{
    RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now);
    RenderedSessionView RenderCancelled(SessionState state, string viewer, DateTimeOffset now) => throw new NotSupportedException();
    SessionViewSnapshot Snapshot(SessionState state, DateTimeOffset now) => throw new NotSupportedException();
    RenderedSessionView? Render(SessionViewSnapshot snapshot, IFormat format, string viewer) => throw new NotSupportedException();
}

internal sealed class ViewRenderer : IViewRenderer
{
    public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) =>
        Render(Snapshot(state, now), format, viewer)
        ?? throw new InvalidOperationException("Only seated participants can receive a session view.");

    public RenderedSessionView RenderCancelled(SessionState state, string viewer, DateTimeOffset now)
    {
        var snapshot = Snapshot(state, now);
        if (!state.Seats.TryGetValue(viewer, out var viewerSeat))
            throw new InvalidOperationException("Only seated participants can receive a session view.");
        var seats = state.Seats.Values.OrderBy(seat => seat.Number).ThenBy(seat => seat.Player, StringComparer.Ordinal).ToArray();
        return new RenderedSessionView(
            state.Session.Version,
            Serialize(snapshot.Shared, null, ToMySeat(viewerSeat), RenderRoster(state.Session.Status, seats, viewer, snapshot.Now), Empty));
    }

    public SessionViewSnapshot Snapshot(SessionState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new SessionViewSnapshot(state, now);
    }

    public RenderedSessionView? Render(SessionViewSnapshot snapshot, IFormat format, string viewer)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewer);
        var state = snapshot.State;
        if (!state.Seats.TryGetValue(viewer, out var viewerSeat)) return null;
        var viewing = new ViewingData(state, viewer);
        var body = JsonSerializer.SerializeToElement(format.View(viewing));
        var deadline = format.Deadline(viewing);
        var seats = state.Seats.Values.OrderBy(seat => seat.Number).ThenBy(seat => seat.Player, StringComparer.Ordinal).ToArray();
        var roster = RenderRoster(state.Session.Status, seats, viewer, snapshot.Now);
        return new RenderedSessionView(state.Session.Version, Serialize(snapshot.Shared, deadline, ToMySeat(viewerSeat), roster, body));
    }

    private static RosterView RenderRoster(string status, Seat[] seats, string viewer, DateTimeOffset now)
    {
        var ordered = status is "finished" or "cancelled"
            ? seats.OrderBy(seat => seat.Place ?? int.MaxValue).ThenBy(seat => seat.Number).ThenBy(seat => seat.Player, StringComparer.Ordinal).ToArray()
            : seats;
        if (ordered.Length <= 50) return new CompleteRosterView(ordered.Select(seat => ToRosterSeat(seat, now)).ToArray());
        var index = Array.FindIndex(ordered, seat => seat.Player == viewer);
        var neighbours = index < 0 ? [] : ordered.Skip(Math.Max(0, index - 2)).Take(5).Select(seat => ToRosterSeat(seat, now)).ToArray();
        return new WindowedRosterView(ordered.Length, ordered.Take(10).Select(seat => ToRosterSeat(seat, now)).ToArray(), neighbours);
    }

    private static MySeat ToMySeat(Seat seat) => new(seat.Player, seat.Number, seat.Role, SeatStatusText(seat), seat.Score, seat.Place, seat.Outcome);
    private static SessionRosterSeat ToRosterSeat(Seat seat, DateTimeOffset now) => new(seat.Player, seat.DisplayName ?? seat.Player, seat.Number,
        seat.Role, SeatStatusText(seat), seat.Score, seat.Place, seat.Outcome,
        !seat.IsGuest && seat.LastSeenAt >= now - PresenceTiming.OnlineWindow);
    private static string SeatStatusText(Seat seat) => seat.Status.ToString().ToLowerInvariant();
    private static string Serialize(JsonElement shared, DateTimeOffset? deadline, MySeat me, RosterView roster, JsonElement body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in shared.EnumerateObject()) property.WriteTo(writer);
            writer.WritePropertyName("Deadline"); JsonSerializer.Serialize(writer, deadline);
            writer.WritePropertyName("Me"); JsonSerializer.Serialize(writer, me);
            writer.WritePropertyName("Roster"); JsonSerializer.Serialize(writer, roster, roster.GetType());
            writer.WritePropertyName("Body"); body.WriteTo(writer);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

    private sealed class ViewingData(SessionState state, string viewer) : IViewing
    {
        public SessionRow Session => state.Session;
        public JsonElement State => state.FormatState.Clone();
        public IReadOnlyList<Seat> Seats => state.Seats.Values.OrderBy(seat => seat.Player, StringComparer.Ordinal).ToArray();
        public IReadOnlyList<SessionItem> Items => state.Items.Values.OrderBy(item => item.Metadata.Slot).Select(item => item.Metadata).ToArray();
        public int ItemCount => state.ItemCount;
        public string Viewer => viewer;

        public StoredAnswer? Answer(string player, int slot) =>
            player != viewer && !Session.Ended ? null : AnswerAt(player, slot);

        public bool HasAnswered(string player, int slot) => state.Answers.ContainsKey(new AnswerKey(player, slot));
        public JsonElement Card(int slot) => Item(slot).Card.Clone();
        public JsonElement? Reveal(int slot) => state.Seats.TryGetValue(viewer, out var seat) && seat.ReleasedSlot >= slot
            ? Item(slot).Reveal.Clone() : null;
        public IReadOnlyList<AnswerRecord> Answers(string player) => state.Answers.Where(answer => answer.Key.Player == player)
            .OrderBy(answer => answer.Key.Slot).Select(answer => answer.Value).ToArray();

        private StoredAnswer? AnswerAt(string player, int slot) => state.Answers.TryGetValue(new AnswerKey(player, slot), out var answer)
            ? new StoredAnswer(answer.Stored?.Clone(), answer.Revision, answer.Miss) : null;
        private SessionItemState Item(int slot) => state.Items.TryGetValue(slot, out var item)
            ? item : throw new KeyNotFoundException($"Slot {slot} is not loaded.");
    }
}
