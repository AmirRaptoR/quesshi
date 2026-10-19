using Quesshi.Plugin.Api;

namespace Quesshi.Application.Content;

/// <summary>Registers sources once and makes their capability definitions available to tenants.</summary>
internal sealed class SourceRegistry(CapabilityRegistry capabilities) : ISourceRegistry
{
    private readonly Dictionary<string, ISource> _sources = new(StringComparer.Ordinal);

    public void Register(ISource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Id);
        ArgumentNullException.ThrowIfNull(source.Params);
        ArgumentNullException.ThrowIfNull(source.Settings);

        var id = source.Id.Trim();
        if (_sources.ContainsKey(id)) throw new InvalidOperationException($"Source '{id}' is already registered.");
        capabilities.Register(CapabilityType.Source, id, source.Settings);
        _ = _sources.TryAdd(id, source);
    }

    public ISource Get(string id) => TryGet(id, out var source)
        ? source!
        : throw new KeyNotFoundException($"Source '{id}' is not registered.");

    public bool TryGet(string id, out ISource? source) => _sources.TryGetValue(id, out source);

    public static SourceRegistry Create(CapabilityRegistry capabilities, IEnumerable<ISource> sources)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(sources);
        var registry = new SourceRegistry(capabilities);
        foreach (var source in sources) registry.Register(source);
        return registry;
    }
}
