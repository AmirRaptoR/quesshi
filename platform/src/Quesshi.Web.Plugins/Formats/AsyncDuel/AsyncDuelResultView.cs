using System.Text.Json;
using Quesshi.Shared;

namespace Quesshi.Web.Plugins.Formats.AsyncDuel;

/// <summary>One player's finished run: per question whether it was right (true), wrong or missed (false), and the stored answer.</summary>
public sealed record ResultRun(string Player, IReadOnlyList<bool?> Outcomes, IReadOnlyList<JsonElement?> Stored);

/// <summary>The finished duel's runs as the view releases them once it has ended; nothing is read beyond what the view carries.</summary>
public sealed record AsyncDuelResultView(int Total, ResultRun? You, IReadOnlyList<ResultRun> Others)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static AsyncDuelResultView Empty { get; } = new(0, null, []);

    /// <summary>An absent, null or malformed body reads as empty, so the screen still shows the standings the envelope carries.</summary>
    public static AsyncDuelResultView From(SessionView view)
    {
        if (view.Body.ValueKind != JsonValueKind.Object) return Empty;
        try
        {
            var wire = view.Body.Deserialize<Wire>(Options);
            if (wire is null) return Empty;
            var you = wire.You is null ? null : Run(wire.You);
            var others = (wire.Others ?? []).Select(Run).ToArray();
            var total = Math.Max(Count(view.Content), Math.Max(wire.You?.Total ?? 0, others.Concat(you is null ? [] : [you]).Select(run => run.Outcomes.Count).DefaultIfEmpty(0).Max()));
            return new AsyncDuelResultView(total, you, others);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static int Count(JsonElement content) =>
        content.ValueKind == JsonValueKind.Object && content.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var value)
            ? value : 0;

    private static ResultRun Run(WireRun run)
    {
        var answers = run.Answers ?? [];
        return new ResultRun(
            run.Player ?? "",
            answers.Select(answer => (bool?)(!answer.Miss && answer.Credit > 0)).ToArray(),
            answers.Select(answer => answer.Stored is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } stored ? (JsonElement?)stored : null).ToArray());
    }

    private sealed record WireAnswer(JsonElement? Stored, double Credit, bool Miss);
    private sealed record WireRun(string? Player, IReadOnlyList<WireAnswer>? Answers, int? Total);
    private sealed record Wire(WireRun? You, IReadOnlyList<WireRun>? Others);
}
