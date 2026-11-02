namespace Quesshi.Web.Plugins.Formats.AsyncDuel;

/// <summary>The client side of the <c>async_duel</c> format: its lobby settings, running screen and result.</summary>
public sealed class AsyncDuelClient : IFormatClient
{
    public const string FormatId = "async_duel";

    public string Id => FormatId;
    public Type Settings => typeof(AsyncDuelSettings);
    public Type Screen => typeof(AsyncDuelScreen);
    public Type Result => typeof(AsyncDuelResult);
    public bool Matchmaking => true;
}
