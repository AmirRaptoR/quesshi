using System.Collections.ObjectModel;
using System.Text.Json;
using Quesshi.Engine.Contracts;

namespace Quesshi.Application.Sessions;

/// <summary>All data a transition may change. Each update produces a separate state instance.</summary>
public sealed record SessionState(
    SessionRow Session,
    JsonElement FormatState,
    IReadOnlyDictionary<string, Seat> Seats,
    IReadOnlyDictionary<int, SessionItemState> Items,
    IReadOnlyDictionary<AnswerKey, AnswerState> Answers)
{
    public static SessionState Create(
        SessionRow session,
        JsonElement formatState,
        IEnumerable<Seat> seats,
        IEnumerable<SessionItemState> items) => new(
            session,
            formatState.Clone(),
            ReadOnly(seats.ToDictionary(seat => seat.Player, StringComparer.Ordinal)),
            ReadOnly(items.ToDictionary(item => item.Metadata.Slot)),
            ReadOnly(new Dictionary<AnswerKey, AnswerState>()));

    internal static IReadOnlyDictionary<TKey, TValue> ReadOnly<TKey, TValue>(Dictionary<TKey, TValue> values) where TKey : notnull =>
        new ReadOnlyDictionary<TKey, TValue>(values);
}

/// <summary>Engine-only snapshot fields used for kind grading; formats receive only Metadata.</summary>
public sealed record SessionItemState(
    SessionItem Metadata,
    JsonElement Payload,
    JsonElement Resolved,
    JsonElement Card,
    JsonElement Reveal)
{
    public static SessionItemState Create(
        SessionItem metadata,
        JsonElement payload,
        JsonElement resolved,
        JsonElement card,
        JsonElement reveal) => new(metadata, payload.Clone(), resolved.Clone(), card.Clone(), reveal.Clone());
}

public sealed record AnswerKey(string Player, int Slot);

public sealed record AnswerState(
    JsonElement? Stored,
    bool IsGraded,
    double Credit,
    int Points,
    TimeSpan Taken,
    DateTimeOffset Received,
    bool Miss = false,
    int Revision = 0);
