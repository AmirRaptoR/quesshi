using System.Text.Json;
using Quesshi.Shared;

namespace Quesshi.Web.Plugins.Formats.AsyncDuel;

/// <summary>How the viewer's most recent answer to the current slot ended.</summary>
public enum RunOutcome { None, Correct, Wrong, Missed }

/// <summary>The viewer's own run, read once per view. Nothing about any other player is read.</summary>
public sealed record AsyncDuelBodyView(
    string? Kind,
    int? KindVersion,
    JsonElement? Card,
    JsonElement? Reveal,
    JsonElement? Stored,
    RunOutcome Outcome,
    int Points,
    int? Total)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static AsyncDuelBodyView Empty { get; } = new(null, null, null, null, null, RunOutcome.None, 0, null);

    /// <summary>An absent, null or malformed body reads as empty, so the screen shows what the envelope alone says.</summary>
    public static AsyncDuelBodyView From(SessionView view)
    {
        if (view.Body.ValueKind != JsonValueKind.Object) return Empty;
        try
        {
            var you = view.Body.Deserialize<Wire>(Options)?.You;
            if (you is null) return Empty;
            var answer = you.Answers is { Count: > 0 } answers && view.Slot >= 0 && view.Slot < answers.Count ? answers[view.Slot] : null;
            var outcome = answer is null ? RunOutcome.None : answer.Miss ? RunOutcome.Missed : answer.Credit > 0 ? RunOutcome.Correct : RunOutcome.Wrong;
            return new AsyncDuelBodyView(you.Kind, you.KindVersion, Present(you.Card), Present(you.Reveal), Present(answer?.Stored),
                outcome, answer?.Points ?? 0, you.Total);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static JsonElement? Present(JsonElement? value) => value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } ? value : null;

    private sealed record WireAnswer(JsonElement? Stored, double Credit, int Points, bool Miss);
    private sealed record WireRun(string? Kind, int? KindVersion, JsonElement? Card, JsonElement? Reveal, IReadOnlyList<WireAnswer>? Answers, int? Total);
    private sealed record Wire(WireRun? You);
}
