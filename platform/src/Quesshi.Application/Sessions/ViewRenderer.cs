using System.Text.Json;
using Quesshi.Engine.Contracts;

namespace Quesshi.Application.Sessions;

public sealed record SessionView(long Version, string Json);

/// <summary>Shared command and stream view boundary. It owns no persistence or transition state.</summary>
public interface IViewRenderer
{
    SessionView Render(SessionState state, IFormat format, string viewer);
}

internal sealed class ViewRenderer : IViewRenderer
{
    public SessionView Render(SessionState state, IFormat format, string viewer)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewer);
        var view = format.View(new ViewingData(state, viewer));
        return new SessionView(state.Session.Version, JsonSerializer.Serialize(view));
    }

    private sealed class ViewingData(SessionState state, string viewer) : IViewing
    {
        public SessionRow Session => state.Session;
        public JsonElement State => state.FormatState.Clone();
        public IReadOnlyList<Seat> Seats => state.Seats.Values.OrderBy(seat => seat.Player, StringComparer.Ordinal).ToArray();
        public IReadOnlyList<SessionItem> Items => state.Items.Values.OrderBy(item => item.Metadata.Slot).Select(item => item.Metadata).ToArray();
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
