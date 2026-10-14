using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.Order;
using Quesshi.Kinds.PollChoice;
using Quesshi.Kinds.PollRoster;
using Quesshi.Shared;
using Quesshi.Web.Plugins;

namespace Quesshi.Web.Tests;

/// <summary>Exercises the eagerly-registered clients with the published kind fixtures through their hosts.</summary>
public sealed class EagerKindFixtureHostTests : BunitContext
{
    public EagerKindFixtureHostTests() => Services.AddSingleton<IClientRegistry>(Registry());

    [Fact]
    public void Choice_fixture_runs_through_play_reveal_and_editor_hosts()
    {
        using var fixture = Fixture("choice");
        var kind = new ChoiceKind();
        var payload = kind.Upgrade(1, Required(fixture.RootElement, "payload"));
        var item = new Item<ChoicePayload, ChoiceResolved>(payload, kind.Resolve(payload, Context(Required(fixture.RootElement, "seed"))));
        var card = FixtureValue(Required(fixture.RootElement, "card"), ChoiceJsonContext.Default.ChoiceCard);
        object? answer = null;

        RenderPlay(kind.Id, card, value => answer = value).Find("[data-choice='2']").Click();
        Assert.Equal(new ChoiceStored(2), kind.Normalize(item, Assert.IsType<ChoiceAnswer>(answer)));
        AssertDisabled(kind.Id, card, "[data-choice='2']");

        var reveal = RenderReveal(card, FixtureValue(Required(fixture.RootElement, "reveal"), ChoiceJsonContext.Default.ChoiceReveal), Stored(fixture.RootElement, ChoiceJsonContext.Default.ChoiceStored));
        Assert.Equal(["Three", "One"], reveal.FindAll("[data-answer-index]").Select(element => element.TextContent));

        AssertEditorRoundTrip(kind.Id, payload);
        AssertFixtureValidation(fixture.RootElement, kind.Id, value => kind.Upgrade(1, value));
    }

    [Fact]
    public void Order_fixture_runs_through_play_reveal_and_editor_hosts()
    {
        using var fixture = Fixture("order");
        var kind = new OrderKind();
        var payload = kind.Upgrade(1, Required(fixture.RootElement, "payload"));
        var item = new Item<OrderPayload, OrderResolved>(payload, kind.Resolve(payload, Context(Required(fixture.RootElement, "seed"))));
        var card = FixtureValue(Required(fixture.RootElement, "card"), OrderJsonContext.Default.OrderCard);
        object? answer = null;

        RenderPlay(kind.Id, card, value => answer = value).Find("[data-order-submit]").Click();
        Assert.NotNull(kind.Normalize(item, Assert.IsType<OrderAnswer>(answer)));
        AssertDisabled(kind.Id, card, "[data-order-submit]");

        var reveal = RenderReveal(card, FixtureValue(Required(fixture.RootElement, "reveal"), OrderJsonContext.Default.OrderReveal), Stored(fixture.RootElement, OrderJsonContext.Default.OrderStored));
        Assert.Equal(["Delta, Bravo, Alpha, Charlie", "Alpha, Bravo, Charlie, Delta"], reveal.FindAll("[data-stored-order]").Select(element => element.TextContent));

        AssertEditorRoundTrip(kind.Id, payload);
        AssertFixtureValidation(fixture.RootElement, kind.Id, value => kind.Upgrade(1, value));
    }

    [Fact]
    public void Poll_choice_fixture_runs_through_play_reveal_and_editor_hosts()
    {
        using var fixture = Fixture("poll_choice");
        var kind = new PollChoiceKind();
        var payload = kind.Upgrade(1, Required(fixture.RootElement, "payload"));
        var item = new Item<PollChoicePayload, PollChoiceResolved>(payload, kind.Resolve(payload, Context(Required(fixture.RootElement, "seed"))));
        var card = FixtureValue(Required(fixture.RootElement, "card"), PollChoiceJsonContext.Default.PollChoiceCard);
        object? answer = null;
        var play = RenderPlay(kind.Id, card, value => answer = value);

        play.Find("[data-option='green']").Click();
        Assert.Equal(new PollChoiceStored("green", null), kind.Normalize(item, Assert.IsType<PollChoiceAnswer>(answer)));
        play.Find("[data-not-applicable]").Click();
        Assert.Equal(new PollChoiceStored(null, true), kind.Normalize(item, Assert.IsType<PollChoiceAnswer>(answer)));
        AssertDisabled(kind.Id, card, "[data-option='green']");

        var reveal = RenderReveal(card, FixtureValue(Required(fixture.RootElement, "reveal"), PollChoiceJsonContext.Default.PollChoiceReveal), Stored(fixture.RootElement, PollChoiceJsonContext.Default.PollChoiceStored));
        Assert.Equal(["Green", "Not applicable"], reveal.FindAll(".voting-answers li").Select(element => element.TextContent));

        AssertEditorRoundTrip(kind.Id, payload);
        AssertFixtureValidation(fixture.RootElement, kind.Id, value => kind.Upgrade(1, value));
    }

    [Fact]
    public void Poll_roster_fixture_runs_through_play_reveal_and_editor_hosts()
    {
        using var fixture = Fixture("poll_roster");
        var kind = new PollRosterKind();
        var payload = kind.Upgrade(1, Required(fixture.RootElement, "payload"));
        var item = new Item<PollRosterPayload, PollRosterResolved>(payload, kind.Resolve(payload, Context(Required(fixture.RootElement, "seed"))));
        var card = FixtureValue(Required(fixture.RootElement, "card"), PollRosterJsonContext.Default.PollRosterCard);
        object? answer = null;
        var play = RenderPlay(kind.Id, card, value => answer = value);

        foreach (var selector in new[] { "[data-seat='4']", "[data-several]", "[data-none]", "[data-not-applicable]" })
        {
            play.Find(selector).Click();
            Assert.NotNull(kind.Normalize(item, Assert.IsType<PollRosterAnswer>(answer)));
        }
        AssertDisabled(kind.Id, card, "[data-seat='4']");

        var reveal = RenderReveal(card, FixtureValue(Required(fixture.RootElement, "reveal"), PollRosterJsonContext.Default.PollRosterReveal), Stored(fixture.RootElement, PollRosterJsonContext.Default.PollRosterStored));
        Assert.Equal(["Dee", "Several"], reveal.FindAll(".voting-answers li").Take(2).Select(element => element.TextContent));

        AssertEditorRoundTrip(kind.Id, payload);
        AssertFixtureValidation(fixture.RootElement, kind.Id, value => kind.Upgrade(1, value));
    }

    private IRenderedComponent<KindPlay> RenderPlay(string kind, object card, Action<object?> callback, bool disabled = false) =>
        Render<KindPlay>(parameters => parameters
            .Add(component => component.Card, new KindCard(kind, 1, card))
            .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, callback))
            .Add(component => component.Disabled, disabled));

    private IRenderedComponent<KindReveal> RenderReveal(object card, object reveal, object answers) =>
        Render<KindReveal>(parameters => parameters
            .Add(component => component.Card, new KindCard(KindId(card), 1, card))
            .Add(component => component.Reveal, reveal)
            .Add(component => component.Answers, answers));

    private void AssertDisabled(string kind, object card, string selector)
    {
        object? emitted = null;
        var cut = RenderPlay(kind, card, value => emitted = value, disabled: true);
        cut.Find(selector).Click();
        Assert.Null(emitted);
    }

    private void AssertEditorRoundTrip(string kind, object payload)
    {
        object? emitted = null;
        var cut = Render<KindEditor>(parameters => parameters
            .Add(component => component.Kind, kind)
            .Add(component => component.Payload, payload)
            .Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, value => emitted = value))
            .Add(component => component.Lang, "en"));
        cut.Find("[data-payload-save]").Click();
        Assert.Same(payload, emitted);
    }

    private void AssertFixtureValidation(JsonElement fixture, string kind, Func<JsonElement, object> upgrade)
    {
        foreach (var validation in Required(fixture, "validation").EnumerateArray())
        {
            var expected = Required(validation, "codes").EnumerateArray().Select(code => code.GetString()!);
            AssertEditorProblems(kind, upgrade(Required(validation, "payload")), expected);
        }
    }

    private void AssertEditorProblems(string kind, object payload, IEnumerable<string> expected)
    {
        var cut = Render<KindEditor>(parameters => parameters
            .Add(component => component.Kind, kind)
            .Add(component => component.Payload, payload)
            .Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { }))
            .Add(component => component.Lang, "en"));
        Assert.Equal(expected, cut.FindAll("[data-problem]").Select(element => element.GetAttribute("data-problem")));
    }

    private static JsonDocument Fixture(string kind) => JsonDocument.Parse(File.ReadAllText(FixturePath(kind)));

    private static string FixturePath(string kind)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "platform", "tests", "Quesshi.Kinds.Tests", "Fixtures", kind, "basic.json");
            if (File.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException($"Could not locate the {kind} fixture.");
    }

    private static T FixtureValue<T>(JsonElement value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class =>
        JsonSerializer.Deserialize(value, type) ?? throw new JsonException("Fixture value is invalid.");

    private static T[] Stored<T>(JsonElement fixture, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class =>
        Required(fixture, "answers").EnumerateArray()
            .Where(answer => answer.TryGetProperty("stored", out _))
            .Take(2)
            .Select(answer => FixtureValue(Required(answer, "stored"), type))
            .ToArray();

    private static ResolveContext Context(JsonElement seed) => new(
        Required(seed, "sessionId").GetString()!,
        Required(seed, "slot").GetInt32(),
        seed.TryGetProperty("roster", out var roster)
            ? roster.EnumerateArray().Select(value => new RosterSeat(
                Required(value, "seat").GetInt32(),
                Required(value, "role").GetString()!,
                Required(value, "status").GetString()!,
                Required(value, "name").GetString()!)).ToArray()
            : []);

    private static JsonElement Required(JsonElement source, string name) => source.TryGetProperty(name, out var value)
        ? value
        : throw new InvalidDataException($"Fixture is missing '{name}'.");

    private static string KindId(object card) => card switch
    {
        ChoiceCard => "choice",
        OrderCard => "order",
        PollChoiceCard => "poll_choice",
        PollRosterCard => "poll_roster",
        _ => throw new ArgumentOutOfRangeException(nameof(card))
    };

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
