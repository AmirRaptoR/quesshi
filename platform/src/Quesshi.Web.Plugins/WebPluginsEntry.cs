namespace Quesshi.Web.Plugins;

/// <summary>Registers the client components compiled into the Web shell.</summary>
public sealed class WebPluginsEntry : IKindClientAssemblyEntry
{
    public void Register(IClientRegistry registry) => registry.Register(new TestKindClient());
}

public sealed class TestKindClient : IKindClient
{
    public string Id => "test";
    public Type? Play(int version) => version switch
    {
        1 => typeof(TestKindPlayV1),
        2 => typeof(TestKindPlayV2),
        _ => null
    };

    public Type? Reveal(int version) => version switch
    {
        1 => typeof(TestKindRevealV1),
        2 => typeof(TestKindRevealV2),
        _ => null
    };

    public Type Editor => typeof(TestKindEditor);
}
