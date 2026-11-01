namespace Quesshi.Web.Plugins;

/// <summary>
/// How a format screen sends a command to its session. The shell positions it at the currently
/// accepted view and feeds the response back into the same state the stream feeds.
/// </summary>
public interface IFormatCommands
{
    /// <summary>True when the server accepted the command.</summary>
    Task<bool> SendAsync(string type, object? payload = null, CancellationToken cancellationToken = default);
}
