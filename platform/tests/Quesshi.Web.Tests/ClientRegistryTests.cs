using System.Reflection;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Components;
using Quesshi.Kinds.MapPick.Client;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

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
    public async Task Admin_client_loads_once_and_registers_every_routable_component()
    {
        var loader = new RecordingLoader(typeof(AdminClientEntry).Assembly);
        var registry = new AdminClientRegistry(loader, new RecordingTranslationRegistry());

        await registry.LoadAsync();
        await registry.LoadAsync();

        Assert.Equal(["Quesshi.Web.Admin.dll"], loader.AssemblyNames);
        Assert.Same(typeof(AdminApp), registry.Root);
        Assert.Equal(
            typeof(AdminClientEntry).Assembly.DefinedTypes
                .Where(type => !type.IsAbstract && typeof(IComponent).IsAssignableFrom(type))
                .Where(type => type.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length != 0)
                .Select(type => type.AsType())
                .OrderBy(type => type.FullName),
            registry.Pages.OrderBy(type => type.FullName));
    }

    [Fact]
    public async Task Admin_client_loads_one_root_and_multiple_independent_page_contributions()
    {
        var registry = new AdminClientRegistry(
            new RecordingLoader(typeof(AdminClientEntry).Assembly, typeof(ContributedAdminEntry).Assembly),
            new RecordingTranslationRegistry());

        await registry.LoadAsync();

        Assert.Same(typeof(AdminApp), registry.Root);
        Assert.Contains(typeof(ContributedAdminPage), registry.Pages);
    }

    [Fact]
    public async Task Admin_client_exposes_a_contributed_page_assembly_to_the_router()
    {
        var registry = new AdminClientRegistry(
            new RecordingLoader(typeof(AdminClientEntry).Assembly, typeof(ContributedAdminEntry).Assembly),
            new RecordingTranslationRegistry());

        await registry.LoadAsync();

        Assert.Contains(typeof(ContributedAdminPage).Assembly, registry.PageAssemblies);
    }

    [Fact]
    public async Task Admin_client_contribution_registers_an_english_label_with_the_translator()
    {
        var translator = await LoadContributedTranslatorAsync();

        await translator.UseAsync("en");

        Assert.Equal("Content", translator["content.navigation"]);
    }

    [Fact]
    public async Task Admin_client_contribution_registers_a_persian_label_with_the_translator()
    {
        var translator = await LoadContributedTranslatorAsync();

        await translator.UseAsync("fa");

        Assert.Equal("محتوا", translator["content.navigation"]);
    }

    [Fact]
    public async Task Admin_client_contribution_falls_back_to_english_without_a_tenant_language_translation()
    {
        var translator = await LoadContributedTranslatorAsync();

        await translator.UseAsync("nl");

        Assert.Equal("Content", translator["content.navigation"]);
    }

    private static async Task<Translator> LoadContributedTranslatorAsync()
    {
        var translator = new Translator(new ClientHttp(
            new HttpClient(new TranslationHandler()) { BaseAddress = new Uri("https://tenant.example/") },
            new NoopReloader()));
        var registry = new AdminClientRegistry(
            new RecordingLoader(typeof(AdminClientEntry).Assembly, typeof(ContributedAdminEntry).Assembly),
            translator);

        await registry.LoadAsync();

        return translator;
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

    private sealed class RecordingTranslationRegistry : IClientTranslationRegistry
    {
        public void Register(ClientTranslation translation)
        {
            ArgumentNullException.ThrowIfNull(translation);
        }
    }

    private sealed class TranslationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }

    private sealed class NoopReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
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

    public sealed class ContributedAdminEntry : IAdminClientAssemblyEntry
    {
        public void Register(IAdminClientRegistry registry)
        {
            registry.RegisterPage(typeof(ContributedAdminPage));
            registry.RegisterTranslation(new ClientTranslation("en", EnglishLabels));
            registry.RegisterTranslation(new ClientTranslation("fa", PersianLabels));
        }

        private static IReadOnlyDictionary<string, string> EnglishLabels { get; } = new Dictionary<string, string>
        {
            ["content.navigation"] = "Content",
        };

        private static IReadOnlyDictionary<string, string> PersianLabels { get; } = new Dictionary<string, string>
        {
            ["content.navigation"] = "محتوا",
        };
    }

    [Route("/admin/contributed")]
    public sealed class ContributedAdminPage : ComponentBase;
}
