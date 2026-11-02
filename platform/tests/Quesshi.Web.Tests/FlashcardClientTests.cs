using Bunit;
using Microsoft.AspNetCore.Components;
using Quesshi.Kinds.Flashcard;
using Quesshi.Shared;
using Quesshi.Web.Plugins;
using FlashcardComponents = Quesshi.Web.Plugins.Flashcard;

namespace Quesshi.Web.Tests;

public sealed class FlashcardClientTests : BunitContext
{
    [Fact]
    public async Task Entry_registers_the_flashcard_client_with_its_components()
    {
        var registry = new ClientRegistry(new NoopLoader());
        new WebPluginsEntry().Register(registry);
        var client = await registry.ResolveAsync("flashcard");

        Assert.Same(typeof(FlashcardComponents.FlashcardPlay), client!.Play(1));
        Assert.Same(typeof(FlashcardComponents.FlashcardReveal), client.Reveal(1));
        Assert.Same(typeof(FlashcardComponents.FlashcardEditor), client.Editor);
    }

    [Fact]
    public void Play_renders_the_front_and_submits_a_valid_self_rating()
    {
        object? answer = null;
        var cut = Render<FlashcardComponents.FlashcardPlay>(parameters => parameters
            .Add(component => component.Card, new FlashcardCard())
            .Add(component => component.Front, "Capital of France?")
            .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, value => answer = value)));

        Assert.Equal("Capital of France?", cut.Find("[data-flashcard-front]").TextContent);
        cut.Find("[data-rating='2']").Click();
        var stored = new FlashcardKind().Normalize(new(new FlashcardPayload("Paris"), new FlashcardResolved()), Assert.IsType<FlashcardAnswer>(answer));
        Assert.Equal(new FlashcardStored(2), stored);
    }

    [Fact]
    public void Reveal_renders_the_back_text()
    {
        var cut = Render<FlashcardComponents.FlashcardReveal>(parameters => parameters
            .Add(component => component.Card, new FlashcardCard())
            .Add(component => component.Reveal, new Quesshi.Kinds.Flashcard.FlashcardReveal("Paris"))
            .Add(component => component.Answers, new[] { new FlashcardStored(3) }));

        Assert.Equal("Paris", cut.Find("[data-flashcard-back]").TextContent);
        Assert.Equal("3", cut.Find("[data-rating='3']").TextContent);
    }

    [Fact]
    public void Editor_emits_a_valid_payload_and_shows_a_message_when_the_back_is_empty()
    {
        object? emitted = null;
        var changed = EventCallback.Factory.Create<object?>(this, value => emitted = value);
        var cut = Render<FlashcardComponents.FlashcardEditor>(parameters => parameters
            .Add(component => component.Kind, "flashcard")
            .Add(component => component.Payload, new FlashcardPayload(""))
            .Add(component => component.Lang, "en")
            .Add(component => component.PayloadChanged, changed));

        Assert.Equal(["back.blank"], cut.FindAll("[data-problem]").Select(item => item.GetAttribute("data-problem")));

        cut.Find("[data-flashcard-back]").Input("Paris");
        Assert.Empty(cut.FindAll("[data-problem]"));
        cut.Find("[data-payload-save]").Click();
        var payload = Assert.IsType<FlashcardPayload>(emitted);
        Assert.Equal("Paris", payload.Back);
        Assert.Empty(new FlashcardKind().Validate(payload, new ValidateEnv("en")));

        cut.Find("[data-flashcard-back]").Input("  ");
        Assert.Equal(["back.blank"], cut.FindAll("[data-problem]").Select(item => item.GetAttribute("data-problem")));
    }

    private sealed class NoopLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string _) => Task.FromResult<IReadOnlyList<System.Reflection.Assembly>>([]);
    }
}
