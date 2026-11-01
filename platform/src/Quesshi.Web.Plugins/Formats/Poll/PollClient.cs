namespace Quesshi.Web.Plugins.Formats.Poll;

/// <summary>The client components of the shared poll format.</summary>
public sealed class PollClient : IFormatClient
{
    public string Id => "poll";
    public Type Settings => typeof(PollSettings);
    public Type Screen => typeof(PollScreen);
    public Type Result => typeof(PollResult);
}
