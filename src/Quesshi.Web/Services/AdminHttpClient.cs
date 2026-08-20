namespace Quesshi.Web.Services;

/// <summary>
/// A second HttpClient, so the admin bearer token is never attached to a game request and the game
/// token is never attached to an admin one.
/// </summary>
public sealed class AdminHttpClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}
