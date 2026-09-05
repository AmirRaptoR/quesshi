namespace Quesshi.Domain;

public enum MatchState
{
    AwaitingOpponent = 0,
    InProgress = 1,
    Resolved = 2,
    Forfeited = 3,

    /// <summary>A live duel ended because one side went consecutively silent past the limit.</summary>
    Abandoned = 4,

    /// <summary>A live duel ended with neither side reachable — a server outage, not a forfeit.</summary>
    NoContest = 5
}
