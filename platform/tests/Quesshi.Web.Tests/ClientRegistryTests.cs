using System.Reflection;
using Quesshi.Kinds.MapPick.Client;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;

namespace Quesshi.Web.Tests;

public sealed class ClientRegistryTests
{
    [Fact]
    public async Task Resolves_only_the_registered_kind_and_version()
    {
        var registry = new ClientRegistry(new RecordingLoader());
        registry.Register(new VersionedClient("test"));

        var client = await registry.ResolveAsync("test");

        Assert.Same(typeof(PlayV1), client!.Play(1));
        Assert.Null(client.Play(2));
        Assert.Null(await registry.ResolveAsync("other"));
    }

    [Fact]
    public async Task Loads_a_lazy_entry_once_before_resolving_its_kind()
    {
        var loader = new RecordingLoader(typeof(LazyEntry).Assembly);
        var registry = new ClientRegistry(loader);
        registry.RegisterLazy("lazy", "Quesshi.Test.Lazy.dll");

        var first = await registry.ResolveAsync("lazy");
        var second = await registry.ResolveAsync("lazy");

        Assert.Same(first, second);
        Assert.Equal(["Quesshi.Test.Lazy.dll"], loader.AssemblyNames);
        Assert.Same(typeof(PlayV1), first!.Play(1));
        Assert.Null(first.Play(2));
    }

    [Fact]
    public async Task Resolving_an_eager_kind_does_not_load_the_map_assembly()
    {
        var loader = new RecordingLoader(typeof(LazyEntry).Assembly);
        var registry = new ClientRegistry(loader);
        registry.Register(new VersionedClient("test"));
        registry.RegisterLazy("map_pick", "Quesshi.Kinds.MapPick.Client.dll");

        var client = await registry.ResolveAsync("test");

        Assert.NotNull(client);
        Assert.Empty(loader.AssemblyNames);
    }

    [Fact]
    public async Task Loads_the_configured_map_client_assembly_and_registers_its_components()
    {
        var loader = new RecordingLoader(typeof(MapPickClientEntry).Assembly);
        var registry = new ClientRegistry(loader);
        registry.RegisterLazy("map_pick", "Quesshi.Kinds.MapPick.Client.dll");

        var client = await registry.ResolveAsync("map_pick");
        var resolvedAgain = await registry.ResolveAsync("map_pick");

        Assert.Equal(["Quesshi.Kinds.MapPick.Client.dll"], loader.AssemblyNames);
        Assert.Same(client, resolvedAgain);
        Assert.Same(typeof(MapPickPlay), client!.Play(1));
        Assert.Same(typeof(MapPickReveal), client.Reveal(1));
        Assert.Same(typeof(MapPickEditor), client.Editor);
        Assert.Null(client.Play(2));
        Assert.Null(client.Reveal(2));
    }

    [Fact]
    public async Task Web_plugins_entry_uses_explicit_test_component_types()
    {
        var registry = new ClientRegistry(new RecordingLoader());
        new WebPluginsEntry().Register(registry);

        var client = await registry.ResolveAsync("test");

        Assert.Same(typeof(TestKindPlayV1), client!.Play(1));
        Assert.Same(typeof(TestKindPlayV2), client.Play(2));
        Assert.Same(typeof(TestKindRevealV1), client.Reveal(1));
        Assert.Same(typeof(TestKindRevealV2), client.Reveal(2));
        Assert.Same(typeof(TestKindEditor), client.Editor);
        Assert.Null(client.Play(3));
        Assert.Null(client.Reveal(3));
    }

    [Fact]
    public async Task Admin_client_loads_once_and_registers_each_explicit_page()
    {
        var loader = new RecordingLoader(typeof(AdminClientEntry).Assembly);
        var registry = new AdminClientRegistry(loader);

        await registry.LoadAsync();
        await registry.LoadAsync();

        Assert.Equal(["Quesshi.Web.Admin.dll"], loader.AssemblyNames);
        Assert.Same(typeof(AdminApp), registry.Root);
    }

    [Fact]
    public void Admin_client_entry_registers_explicit_page_components()
    {
        var registry = new RecordingAdminRegistry();

        new AdminClientEntry().Register(registry);

        Assert.Equal([typeof(AdminHome), typeof(AdminLogin), typeof(AdminForgot), typeof(AdminReset), typeof(AdminPassword), typeof(AdminAccounts)], registry.Pages);
    }

    private sealed class RecordingLoader(params Assembly[] assemblies) : IClientAssemblyLoader
    {
        public List<string> AssemblyNames { get; } = [];

        public Task<IReadOnlyList<Assembly>> LoadAsync(string assemblyName)
        {
            AssemblyNames.Add(assemblyName);
            return Task.FromResult<IReadOnlyList<Assembly>>(assemblies);
        }
    }

    private sealed class RecordingAdminRegistry : IAdminClientRegistry
    {
        public Type? Root { get; private set; }
        public List<Type> Pages { get; } = [];
        public Task LoadAsync() => Task.CompletedTask;
        public void RegisterRoot(Type component) => Root = component;
        public void RegisterPage(Type component) => Pages.Add(component);
    }

    public sealed class LazyEntry : IKindClientAssemblyEntry
    {
        public void Register(IClientRegistry registry) => registry.Register(new VersionedClient("lazy"));
    }

    private sealed class VersionedClient(string id) : IKindClient
    {
        public string Id => id;
        public Type? Play(int version) => version == 1 ? typeof(PlayV1) : null;
        public Type? Reveal(int version) => version == 1 ? typeof(RevealV1) : null;
        public Type Editor => typeof(Editor);
    }

    private sealed class PlayV1;
    private sealed class RevealV1;
    private sealed class Editor;
}
