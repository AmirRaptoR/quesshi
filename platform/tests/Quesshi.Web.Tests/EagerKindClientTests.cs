using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.Order;
using Quesshi.Kinds.PollChoice;
using Quesshi.Kinds.PollRoster;
using Quesshi.Shared;
using Quesshi.Web.Plugins;
using ChoiceComponents = Quesshi.Web.Plugins.Choice;
using OrderComponents = Quesshi.Web.Plugins.Order;
using PollChoiceComponents = Quesshi.Web.Plugins.PollChoice;
using PollRosterComponents = Quesshi.Web.Plugins.PollRoster;

namespace Quesshi.Web.Tests;

public sealed class EagerKindClientTests : BunitContext
{
    private static readonly ChoiceCard ChoiceCard = new(["One", "Two", "Three", "Four"]);
    private static readonly OrderCard OrderCard = new([new("d", "Four"), new("b", "Two"), new("a", "One"), new("c", "Three")]);
    private static readonly PollChoiceCard PollChoiceCard = new([new("green", "Green"), new("blue", "Blue")]);
    private static readonly PollRosterCard PollRosterCard = new([new(4, "Ada"), new(8, "Bea")]);

    [Fact]
    public async Task Entry_registers_the_four_eager_clients_with_their_explicit_components()
    {
        var registry = Registry();
        var choice = await registry.ResolveAsync("choice");
        var order = await registry.ResolveAsync("order");
        var pollChoice = await registry.ResolveAsync("poll_choice");
        var pollRoster = await registry.ResolveAsync("poll_roster");

        Assert.Same(typeof(ChoiceComponents.ChoicePlay), choice!.Play(1));
        Assert.Same(typeof(ChoiceComponents.ChoiceReveal), choice.Reveal(1));
        Assert.Same(typeof(ChoiceComponents.ChoiceEditor), choice.Editor);
        Assert.Same(typeof(OrderComponents.OrderPlay), order!.Play(1));
        Assert.Same(typeof(OrderComponents.OrderReveal), order.Reveal(1));
        Assert.Same(typeof(OrderComponents.OrderEditor), order.Editor);
        Assert.Same(typeof(PollChoiceComponents.PollChoicePlay), pollChoice!.Play(1));
        Assert.Same(typeof(PollChoiceComponents.PollChoiceReveal), pollChoice.Reveal(1));
        Assert.Same(typeof(PollChoiceComponents.PollChoiceEditor), pollChoice.Editor);
        Assert.Same(typeof(PollRosterComponents.PollRosterPlay), pollRoster!.Play(1));
        Assert.Same(typeof(PollRosterComponents.PollRosterReveal), pollRoster.Reveal(1));
        Assert.Same(typeof(PollRosterComponents.PollRosterEditor), pollRoster.Editor);
    }

    [Fact]
    public void Play_emits_answer_shapes_accepted_by_their_kind_normalizers()
    {
        object? answer = null;
        var choice = Render<ChoiceComponents.ChoicePlay>(parameters => parameters
            .Add(component => component.Card, ChoiceCard)
            .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, value => answer = value)));
        choice.Find("[data-choice='2']").Click();
        Assert.Equal(new ChoiceStored(2), new ChoiceKind().Normalize(new(new ChoicePayload(ChoiceCard.Options, 2), new ChoiceResolved()), Assert.IsType<ChoiceAnswer>(answer)));

        answer = null;
        var pollChoice = Render<PollChoiceComponents.PollChoicePlay>(parameters => parameters
            .Add(component => component.Card, PollChoiceCard)
            .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, value => answer = value)));
        pollChoice.Find("[data-option='green']").Click();
        Assert.Equal(new PollChoiceStored("green", null), new PollChoiceKind().Normalize(new(new PollChoicePayload(PollChoiceCard.Options), new PollChoiceResolved()), Assert.IsType<PollChoiceAnswer>(answer)));
        pollChoice.Find("[data-not-applicable]").Click();
        Assert.Equal(new PollChoiceStored(null, true), new PollChoiceKind().Normalize(new(new PollChoicePayload(PollChoiceCard.Options), new PollChoiceResolved()), Assert.IsType<PollChoiceAnswer>(answer)));

        var rosterItem = new Item<PollRosterPayload, PollRosterResolved>(new(), new(PollRosterCard.Options));
        answer = null;
        var pollRoster = Render<PollRosterComponents.PollRosterPlay>(parameters => parameters
            .Add(component => component.Card, PollRosterCard)
            .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, value => answer = value)));
        foreach (var selector in new[] { "[data-seat='4']", "[data-several]", "[data-none]", "[data-not-applicable]" })
        {
            pollRoster.Find(selector).Click();
            Assert.NotNull(new PollRosterKind().Normalize(rosterItem, Assert.IsType<PollRosterAnswer>(answer)));
        }
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("order")]
    [InlineData("poll_choice")]
    [InlineData("poll_roster")]
    public void Disabled_play_does_not_emit_an_answer(string kind)
    {
        object? answer = null;
        var card = kind switch { "choice" => (object)ChoiceCard, "order" => OrderCard, "poll_choice" => PollChoiceCard, _ => PollRosterCard };
        var cut = RenderPlay(kind, card, value => answer = value, true);
        Assert.All(cut.FindAll("button"), button => Assert.True(button.HasAttribute("disabled")));
        Assert.Null(answer);
    }

    [Fact]
    public void Reveal_hosts_render_fixture_labels_and_two_stored_answers()
    {
        RenderReveal("choice", ChoiceCard, new ChoiceReveal(2, "Three"), new ChoiceStored[] { new(2), new(0) }).MarkupMatches("<section data-kind=\"choice\" data-version=\"1\" class=\"answer-reveal\"><p data-correct-option=\"2\">Three</p><ul><li data-answer-index=\"2\">Three</li><li data-answer-index=\"0\">One</li></ul></section>");
        Assert.Contains("Four", RenderReveal("order", OrderCard, new OrderReveal(["d", "b", "a", "c"]), new OrderStored[] { new(["d", "b", "a", "c"]), new(["a", "b", "c", "d"]) }).Markup);
        Assert.Contains("Green", RenderReveal("poll_choice", PollChoiceCard, new PollChoiceReveal(), new PollChoiceStored[] { new("green", null), new(null, true) }).Markup);
        Assert.Contains("Several", RenderReveal("poll_roster", PollRosterCard, new PollRosterReveal(), new PollRosterStored[] { new(4, null, null, null), new(null, true, null, null) }).Markup);
    }

    [Fact]
    public void Order_handle_reorders_with_keyboard_and_moves_to_the_rtl_start_edge()
    {
        object? answer = null;
        var cut = Render< OrderComponents.OrderPlay>(parameters => parameters
            .Add(component => component.Card, OrderCard)
            .Add(component => component.Lang, "fa")
            .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, value => answer = value)));
        var grip = cut.Find("[data-grip][data-place='0']");
        grip.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.Find("[data-order-submit]").Click();
        var orderAnswer = Assert.IsType<OrderAnswer>(answer);
        Assert.Equal(["b", "d", "a", "c"], orderAnswer.ItemIds);
        Assert.Equal(["b", "d", "a", "c"], new OrderKind().Normalize(new(new OrderPayload(OrderCard.Items), new OrderResolved(["d", "b", "a", "c"])), orderAnswer).ItemIds);
        Assert.Equal("rtl", cut.Find("[data-kind='order']").GetAttribute("dir"));
        Assert.Equal("BUTTON", cut.Find(".sortrow").FirstElementChild!.TagName);
    }

    [Fact]
    public void Editors_round_trip_valid_payloads_and_show_every_validation_problem()
    {
        var choice = new ChoicePayload(ChoiceCard.Options, 2);
        AssertEditorRoundTrip<ChoiceComponents.ChoiceEditor>("choice", choice);
        AssertEditor("choice", choice, new ChoicePayload([], 9), new ChoiceKind().Validate(new ChoicePayload([], 9), new("en")));

        var order = new OrderPayload(OrderCard.Items);
        AssertEditorRoundTrip<OrderComponents.OrderEditor>("order", order);
        AssertEditor("order", order, new OrderPayload([]), new OrderKind().Validate(new OrderPayload([]), new("en")));

        var pollChoice = new PollChoicePayload(PollChoiceCard.Options);
        AssertEditorRoundTrip<PollChoiceComponents.PollChoiceEditor>("poll_choice", pollChoice);
        AssertEditor("poll_choice", pollChoice, new PollChoicePayload([]), new PollChoiceKind().Validate(new PollChoicePayload([]), new("en")));

        var roster = new PollRosterPayload();
        AssertEditorRoundTrip<PollRosterComponents.PollRosterEditor>("poll_roster", roster);
        var invalidRoster = new PollRosterPayload { Extra = new() { ["unexpected"] = default } };
        AssertEditor("poll_roster", roster, invalidRoster, new PollRosterKind().Validate(invalidRoster, new("en")));
    }

    private IRenderedComponent<IComponent> RenderPlay(string kind, object card, Action<object?> callback, bool disabled = false)
    {
        var answer = EventCallback.Factory.Create<object?>(this, callback);
        return kind switch
        {
            "choice" => Render<ChoiceComponents.ChoicePlay>(parameters => parameters.Add(component => component.Card, card).Add(component => component.OnAnswer, answer).Add(component => component.Disabled, disabled)),
            "order" => Render<OrderComponents.OrderPlay>(parameters => parameters.Add(component => component.Card, card).Add(component => component.OnAnswer, answer).Add(component => component.Disabled, disabled)),
            "poll_choice" => Render<PollChoiceComponents.PollChoicePlay>(parameters => parameters.Add(component => component.Card, card).Add(component => component.OnAnswer, answer).Add(component => component.Disabled, disabled)),
            _ => Render<PollRosterComponents.PollRosterPlay>(parameters => parameters.Add(component => component.Card, card).Add(component => component.OnAnswer, answer).Add(component => component.Disabled, disabled)),
        };
    }

    private IRenderedComponent<IComponent> RenderReveal(string kind, object card, object reveal, object answers)
    {
        return kind switch
        {
            "choice" => Render<ChoiceComponents.ChoiceReveal>(parameters => parameters.Add(component => component.Card, card).Add(component => component.Reveal, reveal).Add(component => component.Answers, answers)),
            "order" => Render<OrderComponents.OrderReveal>(parameters => parameters.Add(component => component.Card, card).Add(component => component.Reveal, reveal).Add(component => component.Answers, answers)),
            "poll_choice" => Render<PollChoiceComponents.PollChoiceReveal>(parameters => parameters.Add(component => component.Card, card).Add(component => component.Reveal, reveal).Add(component => component.Answers, answers)),
            _ => Render<PollRosterComponents.PollRosterReveal>(parameters => parameters.Add(component => component.Card, card).Add(component => component.Reveal, reveal).Add(component => component.Answers, answers)),
        };
    }

    private void AssertEditor(string kind, object valid, object invalid, IReadOnlyList<Problem> expectedProblems)
    {
        Assert.Empty(RenderEditor(kind, valid).FindAll("[data-problem]"));
        Assert.Equal(expectedProblems.Select(problem => problem.Code), RenderEditor(kind, invalid).FindAll("[data-problem]").Select(item => item.GetAttribute("data-problem")));
    }

    private void AssertEditorRoundTrip<TComponent>(string kind, object payload) where TComponent : IComponent
    {
        object? emitted = null;
        var cut = Render<TComponent>(builder =>
        {
            builder.OpenComponent<TComponent>(0);
            builder.AddAttribute(1, "Kind", kind);
            builder.AddAttribute(2, "Payload", payload);
            builder.AddAttribute(3, "PayloadChanged", EventCallback.Factory.Create<object?>(this, value => emitted = value));
            builder.AddAttribute(4, "Lang", "en");
            builder.CloseComponent();
        });

        cut.Find("[data-payload-save]").Click();
        Assert.Same(payload, emitted);
    }

    private IRenderedComponent<IComponent> RenderEditor(string kind, object payload) => kind switch
    {
        "choice" => Render<ChoiceComponents.ChoiceEditor>(parameters => parameters.Add(component => component.Kind, kind).Add(component => component.Payload, payload).Add(component => component.Lang, "en").Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { }))),
        "order" => Render<OrderComponents.OrderEditor>(parameters => parameters.Add(component => component.Kind, kind).Add(component => component.Payload, payload).Add(component => component.Lang, "en").Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { }))),
        "poll_choice" => Render<PollChoiceComponents.PollChoiceEditor>(parameters => parameters.Add(component => component.Kind, kind).Add(component => component.Payload, payload).Add(component => component.Lang, "en").Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { }))),
        _ => Render<PollRosterComponents.PollRosterEditor>(parameters => parameters.Add(component => component.Kind, kind).Add(component => component.Payload, payload).Add(component => component.Lang, "en").Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { }))),
    };

    private void UseRegistry() => Services.AddSingleton<IClientRegistry>(Registry());
    private static ClientRegistry Registry()
    {
        var registry = new ClientRegistry(new NoopLoader());
        new WebPluginsEntry().Register(registry);
        return registry;
    }

    private sealed class NoopLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string _) => Task.FromResult<IReadOnlyList<System.Reflection.Assembly>>([]);
    }
}
