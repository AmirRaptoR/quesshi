namespace Quesshi.Domain;

/// <summary>One player's pass through the questions of a match.</summary>
public sealed class PlayerRun(int total)
{
    private readonly List<AnswerRecord> _answers = [];

    public IReadOnlyList<AnswerRecord> Answers => _answers;
    public DateTimeOffset? ServedAt { get; private set; }

    /// <summary>How many questions this match holds — a duel is no longer always six.</summary>
    public int Total { get; } = total;

    public bool Finished => _answers.Count >= Total;
    public int Score => _answers.Sum(a => a.Score);
    public int Correct => _answers.Count(a => a.Correct);
    public int NextSlot => _answers.Count;

    internal void MarkServed(DateTimeOffset at) => ServedAt = at;

    internal void Record(AnswerRecord answer)
    {
        _answers.Add(answer);
        ServedAt = null;
    }

    internal static PlayerRun Restore(int total, IEnumerable<AnswerRecord> answers, DateTimeOffset? servedAt)
    {
        var run = new PlayerRun(total) { ServedAt = servedAt };
        run._answers.AddRange(answers);
        return run;
    }
}
