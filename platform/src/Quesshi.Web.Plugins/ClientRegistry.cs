using System.Collections.Concurrent;
using System.Reflection;

namespace Quesshi.Web.Plugins;

public interface IClientTranslator
{
    string this[string key] { get; }
}

/// <summary>Accepts client-owned translations that complement the tenant's shared table.</summary>
public interface IClientTranslationRegistry
{
    void Register(ClientTranslation translation);
}

/// <summary>A screen-owned table for one tenant language.</summary>
public sealed record ClientTranslation(string LanguageCode, IReadOnlyDictionary<string, string> Values);

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

/// <summary>Registers the root and route components from the lazy administration client.</summary>
public interface IAdminClientAssemblyEntry
{
    void Register(IAdminClientRegistry registry);
}

public interface IAdminClientRegistry
{
    Type? Root { get; }
    IReadOnlyList<Type> Pages { get; }
    IReadOnlyList<System.Reflection.Assembly> PageAssemblies { get; }
    IReadOnlyList<AdminNavigation> Navigation { get; }
    Task LoadAsync();
    void RegisterRoot(Type component);
    void RegisterPage(Type component);
    void RegisterNavigation(AdminNavigation navigation);
    void RegisterTranslation(ClientTranslation translation);
}

/// <summary>Registers a screen link and the roles that may use it.</summary>
public sealed record AdminNavigation(
    string Href,
    string LabelKey,
    IReadOnlySet<string> Roles,
    Func<AdminNavigationContext, bool>? IsAvailable = null)
{
    public bool IsVisibleTo(AdminNavigationContext context) =>
        Roles.Any(role => string.Equals(role, context.Role, StringComparison.OrdinalIgnoreCase)) && (IsAvailable?.Invoke(context) ?? true);
}

/// <summary>The admin identity details a registered screen may use for navigation availability.</summary>
public sealed record AdminNavigationContext(string? Role, bool IsPlatform);

public sealed class AdminClientRegistry : IAdminClientRegistry
{
    private readonly IClientAssemblyLoader loader;
    private readonly IClientTranslationRegistry translations;
    private readonly Lazy<Task> load;
    private readonly List<Type> pages = [];
    private readonly List<AdminNavigation> navigation = [];

    public AdminClientRegistry(IClientAssemblyLoader loader, IClientTranslationRegistry translations)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(translations);
        this.loader = loader;
        this.translations = translations;
        load = new Lazy<Task>(LoadCoreAsync);
    }

    public Type? Root { get; private set; }
    public IReadOnlyList<Type> Pages => pages;
    public IReadOnlyList<Assembly> PageAssemblies => pages
        .Select(page => page.Assembly)
        .Where(assembly => assembly != Root?.Assembly)
        .Distinct()
        .ToArray();
    public IReadOnlyList<AdminNavigation> Navigation => navigation;

    public Task LoadAsync() => load.Value;

    public void RegisterRoot(Type component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (Root is not null) throw new InvalidOperationException("The admin client root is already registered.");
        Root = component;
    }

    public void RegisterPage(Type component)
    {
        ArgumentNullException.ThrowIfNull(component);
        pages.Add(component);
    }

    public void RegisterNavigation(AdminNavigation contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (string.IsNullOrWhiteSpace(contribution.Href)) throw new ArgumentException("An admin navigation URL is required.", nameof(contribution));
        if (string.IsNullOrWhiteSpace(contribution.LabelKey)) throw new ArgumentException("An admin navigation label key is required.", nameof(contribution));
        navigation.Add(contribution);
    }

    public void RegisterTranslation(ClientTranslation translation)
    {
        ArgumentNullException.ThrowIfNull(translation);
        translations.Register(translation);
    }

    private async Task LoadCoreAsync()
    {
        var entries = (await loader.LoadAsync("Quesshi.Web.Admin.dll"))
            .SelectMany(assembly => assembly.DefinedTypes)
            .Where(type => !type.IsAbstract && typeof(IAdminClientAssemblyEntry).IsAssignableFrom(type))
            .Select(type => (IAdminClientAssemblyEntry?)Activator.CreateInstance(type.AsType()))
            .Where(entry => entry is not null)
            .Cast<IAdminClientAssemblyEntry>()
            .ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("The admin client did not provide an entry point.");
        foreach (var entry in entries) entry.Register(this);
        if (Root is null) throw new InvalidOperationException("The admin client did not register its root component.");
    }
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
