using System.Collections.Concurrent;
using System.Reflection;

namespace Quesshi.Web.Plugins;

/// <summary>Describes the client components for one kind.</summary>
public interface IKindClient
{
    string Id { get; }
    Type? Play(int version);
    Type? Reveal(int version);
    Type Editor { get; }
}

/// <summary>Registers eager clients and resolves clients whose assemblies can be loaded on demand.</summary>
public interface IClientRegistry
{
    void Register(IKindClient client);
    void RegisterLazy(string id, string assemblyName);
    ValueTask<IKindClient?> ResolveAsync(string id);
}

/// <summary>Entry point implemented once by every client assembly.</summary>
public interface IKindClientAssemblyEntry
{
    void Register(IClientRegistry registry);
}

/// <summary>Loads a client assembly without coupling the registry to a lazy client implementation.</summary>
public interface IClientAssemblyLoader
{
    Task<IReadOnlyList<Assembly>> LoadAsync(string assemblyName);
}

public sealed class ClientRegistry(IClientAssemblyLoader loader) : IClientRegistry
{
    private readonly ConcurrentDictionary<string, IKindClient> clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LazyKind> lazyKinds = new(StringComparer.Ordinal);

    public void Register(IKindClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(client.Id)) throw new ArgumentException("A kind client id is required.", nameof(client));
        if (!clients.TryAdd(client.Id, client)) throw new InvalidOperationException($"A client for '{client.Id}' is already registered.");
    }

    public void RegisterLazy(string id, string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        if (!lazyKinds.TryAdd(id, new LazyKind(assemblyName, new Lazy<Task>(() => LoadAsync(assemblyName)))))
            throw new InvalidOperationException($"A lazy client for '{id}' is already registered.");
    }

    public async ValueTask<IKindClient?> ResolveAsync(string id)
    {
        if (clients.TryGetValue(id, out var client)) return client;
        if (!lazyKinds.TryGetValue(id, out var lazyKind)) return null;

        await lazyKind.Load.Value;
        return clients.GetValueOrDefault(id);
    }

    private async Task LoadAsync(string assemblyName)
    {
        var entries = (await loader.LoadAsync(assemblyName))
            .SelectMany(assembly => assembly.DefinedTypes)
            .Where(type => !type.IsAbstract && typeof(IKindClientAssemblyEntry).IsAssignableFrom(type))
            .Select(type => (IKindClientAssemblyEntry?)Activator.CreateInstance(type.AsType()))
            .Where(entry => entry is not null)
            .Cast<IKindClientAssemblyEntry>()
            .ToArray();

        if (entries.Length != 1)
            throw new InvalidOperationException($"Lazy client assembly '{assemblyName}' must contain exactly one {nameof(IKindClientAssemblyEntry)}.");

        entries[0].Register(this);
    }

    private sealed record LazyKind(string AssemblyName, Lazy<Task> Load);
}
