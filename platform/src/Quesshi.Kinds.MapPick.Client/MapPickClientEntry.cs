using Quesshi.Web.Plugins;

namespace Quesshi.Kinds.MapPick.Client;

/// <summary>Registers the lazily loaded map-pick client components.</summary>
public sealed class MapPickClientEntry : IKindClientAssemblyEntry
{
    public void Register(IClientRegistry registry) => registry.Register(new MapPickClient());
}

public sealed class MapPickClient : IKindClient
{
    public string Id => "map_pick";

    public Type? Play(int version) => version == 1 ? typeof(MapPickPlay) : null;

    public Type? Reveal(int version) => version == 1 ? typeof(MapPickReveal) : null;

    public Type Editor => typeof(MapPickEditor);
}
