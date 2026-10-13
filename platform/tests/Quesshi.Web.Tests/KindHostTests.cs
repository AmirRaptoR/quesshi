using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Plugins;

namespace Quesshi.Web.Tests;

public sealed class KindHostTests : BunitContext
{
    [Fact]
    public void Play_forwards_the_card_answer_callback_and_disabled_state_to_the_recorded_version()
    {
        Services.AddSingleton<IClientRegistry>(new FixedRegistry(new HostClient()));
        var answer = EventCallback.Factory.Create<object?>(this, _ => { });

        var cut = Render<KindPlay>(parameters => parameters
            .Add(component => component.Card, new KindCard("test", 1, "card value"))
            .Add(component => component.OnAnswer, answer)
            .Add(component => component.Disabled, true));

        var play = cut.FindComponent<PlayV1>();
        Assert.Equal("card value", play.Instance.Card);
        Assert.Equal(answer, play.Instance.OnAnswer);
        Assert.True(play.Instance.Disabled);
        Assert.Empty(cut.FindComponents<PlayV2>());
    }

    [Fact]
    public void Reveal_forwards_the_card_reveal_and_answers_to_the_recorded_version()
    {
        Services.AddSingleton<IClientRegistry>(new FixedRegistry(new HostClient()));

        var cut = Render<KindReveal>(parameters => parameters
            .Add(component => component.Card, new KindCard("test", 1, "card value"))
            .Add(component => component.Reveal, "reveal value")
            .Add(component => component.Answers, "stored answers"));

        var reveal = cut.FindComponent<RevealV1>();
        Assert.Equal("card value", reveal.Instance.Card);
        Assert.Equal("reveal value", reveal.Instance.Reveal);
        Assert.Equal("stored answers", reveal.Instance.Answers);
        Assert.Empty(cut.FindComponents<RevealV2>());
    }

    [Fact]
    public void Editor_forwards_its_documented_parameters()
    {
        Services.AddSingleton<IClientRegistry>(new FixedRegistry(new HostClient()));
        var payloadChanged = EventCallback.Factory.Create<object?>(this, _ => { });

        var cut = Render<KindEditor>(parameters => parameters
            .Add(component => component.Kind, "test")
            .Add(component => component.Payload, "payload")
            .Add(component => component.PayloadChanged, payloadChanged)
            .Add(component => component.Lang, "fa"));

        var editor = cut.FindComponent<Editor>();
        Assert.Equal("test", editor.Instance.Kind);
        Assert.Equal("payload", editor.Instance.Payload);
        Assert.Equal(payloadChanged, editor.Instance.PayloadChanged);
        Assert.Equal("fa", editor.Instance.Lang);
    }

    [Fact]
    public void Registered_test_kind_is_rendered_through_the_play_host()
    {
        var registry = new ClientRegistry(new NoopLoader());
        new WebPluginsEntry().Register(registry);
        Services.AddSingleton<IClientRegistry>(registry);

        var cut = Render<KindPlay>(parameters => parameters
            .Add(component => component.Card, new KindCard("test", 2, "card"))
            .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, _ => { })));

        Assert.NotNull(cut.Find("[data-kind='test'][data-version='2']"));
    }

    [Theory]
    [InlineData("play")]
    [InlineData("reveal")]
    [InlineData("editor")]
    public void Hosts_render_a_reload_placeholder_for_unknown_or_unsupported_kinds(string host)
    {
        Services.AddSingleton<IClientRegistry>(new FixedRegistry(null));

        var markup = host switch
        {
            "play" => Render<KindPlay>(parameters => parameters
                .Add(value => value.Card, new KindCard("unknown", 1, "card"))
                .Add(value => value.OnAnswer, EventCallback.Factory.Create<object?>(this, _ => { }))).Markup,
            "reveal" => Render<KindReveal>(parameters => parameters
                .Add(value => value.Card, new KindCard("unknown", 1, "card"))
                .Add(value => value.Reveal, "reveal")
                .Add(value => value.Answers, "answers")).Markup,
            _ => Render<KindEditor>(parameters => parameters
                .Add(value => value.Kind, "unknown")
                .Add(value => value.Payload, "payload")
                .Add(value => value.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { }))
                .Add(value => value.Lang, "en")).Markup,
        };

        Assert.Contains("not supported", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reload", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("reveal")]
    public void Play_and_reveal_render_a_reload_placeholder_for_an_unsupported_version(string host)
    {
        Services.AddSingleton<IClientRegistry>(new FixedRegistry(new HostClient()));

        var markup = host == "play"
            ? Render<KindPlay>(parameters => parameters
                .Add(component => component.Card, new KindCard("test", 99, "card"))
                .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, _ => { }))).Markup
            : Render<KindReveal>(parameters => parameters
                .Add(component => component.Card, new KindCard("test", 99, "card"))
                .Add(component => component.Reveal, "reveal")
                .Add(component => component.Answers, "answers")).Markup;

        Assert.Contains("not supported", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reload", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("reveal")]
    [InlineData("editor")]
    public void A_throwing_kind_component_is_contained_by_its_host(string host)
    {
        var client = host switch
        {
            "play" => new HostClient(play: typeof(ThrowingComponent)),
            "reveal" => new HostClient(reveal: typeof(ThrowingComponent)),
            _ => new HostClient(editor: typeof(ThrowingComponent)),
        };
        Services.AddSingleton<IClientRegistry>(new FixedRegistry(client));

        var markup = host switch
        {
            "play" => Render<KindPlay>(parameters => parameters
                .Add(component => component.Card, new KindCard("test", 1, "card"))
                .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, _ => { }))).Markup,
            "reveal" => Render<KindReveal>(parameters => parameters
                .Add(component => component.Card, new KindCard("test", 1, "card"))
                .Add(component => component.Reveal, "reveal")
                .Add(component => component.Answers, "answers")).Markup,
            _ => Render<KindEditor>(parameters => parameters
                .Add(component => component.Kind, "test")
                .Add(component => component.Payload, "payload")
                .Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { }))
                .Add(component => component.Lang, "en")).Markup,
        };

        Assert.Contains("not supported", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("reveal")]
    [InlineData("editor")]
    public void A_host_recovers_from_a_throwing_component_when_its_inputs_change(string host)
    {
        var registry = new MutableRegistry();
        Services.AddSingleton<IClientRegistry>(registry);

        var markup = host switch
        {
            "play" => RecoverPlayHost(registry),
            "reveal" => RecoverRevealHost(registry),
            _ => RecoverEditorHost(registry),
        };

        Assert.DoesNotContain("not supported", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Play_clears_the_previous_component_while_a_new_kind_is_loading()
    {
        var registry = new DelayedRegistry(new HostClient(play: typeof(MarkedPlay)));
        Services.AddSingleton<IClientRegistry>(registry);
        var answer = EventCallback.Factory.Create<object?>(this, _ => { });
        var cut = Render<KindPlay>(parameters => parameters
            .Add(component => component.Card, new KindCard("test", 1, "first"))
            .Add(component => component.OnAnswer, answer));

        Assert.Contains("first", cut.Markup, StringComparison.Ordinal);
        registry.DelayNextResolution();
        cut.Render(parameters => parameters
            .Add(component => component.Card, new KindCard("test", 1, "second"))
            .Add(component => component.OnAnswer, answer));

        Assert.DoesNotContain("first", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Loading kind", cut.Markup, StringComparison.Ordinal);

        registry.CompleteResolution();
        cut.WaitForAssertion(() => Assert.Contains("second", cut.Markup, StringComparison.Ordinal));
    }

    private string RecoverPlayHost(MutableRegistry registry)
    {
        registry.Client = new HostClient(play: typeof(ThrowingComponent));
        var cut = Render<KindPlay>(parameters => parameters
            .Add(component => component.Card, new KindCard("test", 1, "throwing"))
            .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, _ => { })));

        registry.Client = new HostClient(play: typeof(PlayV1));
        cut.Render(parameters => parameters
            .Add(component => component.Card, new KindCard("test", 1, "healthy"))
            .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, _ => { })));
        Assert.NotNull(cut.FindComponent<PlayV1>());
        return cut.Markup;
    }

    private string RecoverRevealHost(MutableRegistry registry)
    {
        registry.Client = new HostClient(reveal: typeof(ThrowingComponent));
        var cut = Render<KindReveal>(parameters => parameters
            .Add(component => component.Card, new KindCard("test", 1, "throwing"))
            .Add(component => component.Reveal, "reveal")
            .Add(component => component.Answers, "answers"));

        registry.Client = new HostClient(reveal: typeof(RevealV1));
        cut.Render(parameters => parameters
            .Add(component => component.Card, new KindCard("test", 1, "healthy"))
            .Add(component => component.Reveal, "reveal")
            .Add(component => component.Answers, "answers"));
        Assert.NotNull(cut.FindComponent<RevealV1>());
        return cut.Markup;
    }

    private string RecoverEditorHost(MutableRegistry registry)
    {
        registry.Client = new HostClient(editor: typeof(ThrowingComponent));
        var cut = Render<KindEditor>(parameters => parameters
            .Add(component => component.Kind, "test")
            .Add(component => component.Payload, "throwing")
            .Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { }))
            .Add(component => component.Lang, "en"));

        registry.Client = new HostClient(editor: typeof(Editor));
        cut.Render(parameters => parameters
            .Add(component => component.Kind, "test")
            .Add(component => component.Payload, "healthy")
            .Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { }))
            .Add(component => component.Lang, "en"));
        Assert.NotNull(cut.FindComponent<Editor>());
        return cut.Markup;
    }

    private sealed class FixedRegistry(IKindClient? client) : IClientRegistry
    {
        public void Register(IKindClient item) => throw new NotSupportedException();
        public void RegisterLazy(string id, string assemblyName) => throw new NotSupportedException();
        public ValueTask<IKindClient?> ResolveAsync(string id) => ValueTask.FromResult(client?.Id == id ? client : null);
    }

    private sealed class MutableRegistry : IClientRegistry
    {
        public IKindClient? Client { get; set; }
        public void Register(IKindClient item) => throw new NotSupportedException();
        public void RegisterLazy(string id, string assemblyName) => throw new NotSupportedException();
        public ValueTask<IKindClient?> ResolveAsync(string id) => ValueTask.FromResult(Client?.Id == id ? Client : null);
    }

    private sealed class DelayedRegistry(IKindClient? client) : IClientRegistry
    {
        private TaskCompletionSource<IKindClient?>? pendingResolution;

        public void Register(IKindClient item) => throw new NotSupportedException();
        public void RegisterLazy(string id, string assemblyName) => throw new NotSupportedException();

        public void DelayNextResolution() => pendingResolution = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteResolution()
        {
            pendingResolution!.SetResult(client);
            pendingResolution = null;
        }

        public ValueTask<IKindClient?> ResolveAsync(string id) => pendingResolution is { } pending
            ? new(pending.Task)
            : ValueTask.FromResult(client?.Id == id ? client : null);
    }

    private sealed class NoopLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) =>
            Task.FromResult<IReadOnlyList<System.Reflection.Assembly>>([]);
    }

    private sealed class HostClient(Type? play = null, Type? reveal = null, Type? editor = null) : IKindClient
    {
        public string Id => "test";
        public Type? Play(int version) => version == 1 ? play ?? typeof(PlayV1) : version == 2 ? typeof(PlayV2) : null;
        public Type? Reveal(int version) => version == 1 ? reveal ?? typeof(RevealV1) : version == 2 ? typeof(RevealV2) : null;
        public Type Editor => editor ?? typeof(Editor);
    }

    private sealed class PlayV1 : ComponentBase
    {
        [Parameter] public object? Card { get; set; }
        [Parameter] public EventCallback<object?> OnAnswer { get; set; }
        [Parameter] public bool Disabled { get; set; }
    }

    private sealed class PlayV2 : ComponentBase;

    private sealed class MarkedPlay : ComponentBase
    {
        [Parameter] public object? Card { get; set; }
        [Parameter] public EventCallback<object?> OnAnswer { get; set; }
        [Parameter] public bool Disabled { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Card?.ToString());
    }

    private sealed class RevealV1 : ComponentBase
    {
        [Parameter] public object? Card { get; set; }
        [Parameter] public object? Reveal { get; set; }
        [Parameter] public object? Answers { get; set; }
    }

    private sealed class RevealV2 : ComponentBase;

    private sealed class Editor : ComponentBase
    {
        [Parameter] public string Kind { get; set; } = string.Empty;
        [Parameter] public object? Payload { get; set; }
        [Parameter] public EventCallback<object?> PayloadChanged { get; set; }
        [Parameter] public string Lang { get; set; } = string.Empty;
    }

    private sealed class ThrowingComponent : ComponentBase
    {
        [Parameter] public object? Card { get; set; }
        [Parameter] public EventCallback<object?> OnAnswer { get; set; }
        [Parameter] public bool Disabled { get; set; }
        [Parameter] public object? Reveal { get; set; }
        [Parameter] public object? Answers { get; set; }
        [Parameter] public string Kind { get; set; } = string.Empty;
        [Parameter] public object? Payload { get; set; }
        [Parameter] public EventCallback<object?> PayloadChanged { get; set; }
        [Parameter] public string Lang { get; set; } = string.Empty;

        protected override void OnParametersSet() => throw new InvalidOperationException("Expected component failure");
    }
}
