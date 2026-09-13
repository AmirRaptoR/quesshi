namespace Quesshi.Grains.Abstractions;

public enum MatchingJoinResult
{
    Joined = 0,
    AlreadyIn = 1,
    Full = 2,
    Started = 3,
    NotFound = 4
}
