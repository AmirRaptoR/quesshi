namespace Quesshi.Web.Plugins.Formats.Live;

/// <summary>The client components of the shared live duel format.</summary>
public sealed class LiveClient : IFormatClient
{
    public string Id => "live";
    public Type Settings => typeof(LiveSettings);
    public Type Screen => typeof(LiveScreen);
    public Type Result => typeof(LiveResult);
}
