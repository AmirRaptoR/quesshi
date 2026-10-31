using System.Text.Json;

namespace Quesshi.Shared;

/// <summary>One session in a player's history, newest joined first.</summary>
public sealed record SessionHistoryEntry(
    Guid Id,
    string Format,
    string Status,
    DateTimeOffset JoinedAt,
    int Score,
    int? Place,
    string? Outcome);

/// <summary>A page of the caller's sessions. <paramref name="Next"/> is the opaque <c>before</c> cursor of the following page, or null at the end.</summary>
public sealed record SessionHistoryPage(IReadOnlyList<SessionHistoryEntry> Sessions, string? Next);

/// <summary>
/// One slot of a finished session as persisted: the public card and reveal exactly as drawn, plus the caller's
/// stored answers for the slot (empty when they did not answer). Each kind client renders its own layout.
/// </summary>
public sealed record SessionQuestionHistoryItem(
    int Slot,
    string Kind,
    int KindVersion,
    string Prompt,
    JsonElement Card,
    JsonElement Reveal,
    IReadOnlyList<JsonElement> Answers);

/// <summary>A page of a finished session's questions in slot order. <paramref name="Next"/> is the <c>after</c> slot of the following page, or null at the end.</summary>
public sealed record SessionQuestionHistoryPage(IReadOnlyList<SessionQuestionHistoryItem> Items, int? Next);
