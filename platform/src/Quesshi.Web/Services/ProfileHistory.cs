using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>Chooses and links the finished sessions the profile shows from the player's session history.</summary>
public static class ProfileHistory
{
    public const int DefaultTake = 12;

    /// <summary>The finished sessions, newest first, at most <paramref name="take"/>; lobby, running and cancelled sessions are left out.</summary>
    public static List<SessionHistoryEntry> Completed(IEnumerable<SessionHistoryEntry> sessions, int take = DefaultTake)
        => [.. sessions.Where(session => string.Equals(session.Status, "finished", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.JoinedAt).ThenBy(session => session.Id).Take(take)];

    /// <summary>The playback page of a finished session.</summary>
    public static string Route(SessionHistoryEntry session) => $"/sessions/{session.Id}";
}
