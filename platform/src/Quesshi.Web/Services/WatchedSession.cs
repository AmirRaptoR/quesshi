namespace Quesshi.Web.Services;

/// <summary>Which session the shell should watch for a location: <c>/s/{id}</c>, or the optional <c>?session={id}</c> query.</summary>
public static class WatchedSession
{
    public static Guid? From(Uri location)
    {
        var segments = location.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is ["s", var id] && Guid.TryParse(id, out var routed)) return routed;
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        return Guid.TryParse(query["session"], out var queried) ? queried : null;
    }
}
