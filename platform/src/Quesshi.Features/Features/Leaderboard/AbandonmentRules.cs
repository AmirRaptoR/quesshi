namespace Quesshi.Features.Leaderboard;

/// <summary>
/// The escalating cost of abandoning a game, copied from today's <c>LiveRules</c>: free the first time
/// in a rolling seven days, then 200, 400, 800 and 1000 for every one after (design 13, 19.4).
/// </summary>
public static class AbandonmentRules
{
    /// <summary>How far back an abandonment still counts against a player.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>The cost of the <paramref name="occurrenceInWindow"/>-th abandonment in the window, counting this one.</summary>
    public static int Penalty(int occurrenceInWindow) => occurrenceInWindow switch
    {
        <= 1 => 0,
        2 => 200,
        3 => 400,
        4 => 800,
        _ => 1000
    };
}

/// <summary>
/// One player's abandonments and score on a single board, copied from today's
/// <c>Player.RecordAbandonment</c>. The platform handler derives the same numbers from its ledger;
/// this keeps today's rule executable, so the two can be compared.
/// </summary>
public sealed class AbandonmentHistory
{
    private readonly List<DateTimeOffset> _abandonments = [];

    public long Score { get; private set; }

    public IReadOnlyList<DateTimeOffset> Abandonments => _abandonments;

    public void AddPoints(long points) => Score = Math.Max(0, Score + points);

    /// <summary>Records an abandonment at <paramref name="at"/>, charges it and returns the penalty.</summary>
    public int RecordAbandonment(DateTimeOffset at)
    {
        var index = _abandonments.BinarySearch(at);
        _abandonments.Insert(index < 0 ? ~index : index, at);

        var windowStart = at - AbandonmentRules.Window;
        var countInWindow = _abandonments.Count(a => a > windowStart && a <= at);

        var penalty = AbandonmentRules.Penalty(countInWindow);
        if (penalty > 0) Score = Math.Max(0, Score - penalty);
        return penalty;
    }
}
