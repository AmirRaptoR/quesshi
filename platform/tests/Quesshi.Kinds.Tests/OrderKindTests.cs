using System.Text.Json;
using Quesshi.Kinds.Order;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class OrderKindTests
{
    private readonly OrderKind _kind = new();

    [Fact]
    public void Metadata_and_validation_follow_order_contract()
    {
        Assert.Equal(("order", 1, "trivia", true), (_kind.Id, _kind.Version, _kind.Family, _kind.Gradable));
        Assert.Empty(_kind.Validate(Payload(("a", " One "), ("b", "Two"), ("c", "Three"), ("d", "Four")), new ValidateEnv("en")));
        Assert.Contains(_kind.Validate(Payload(("a", "one"), ("b", "two"), ("c", "three")), new ValidateEnv("en")), x => x.Code == "items.tooFew");
        Assert.Contains(_kind.Validate(Payload(("a", "one"), ("b", "two"), ("c", "three"), ("d", "four"), ("e", "five")), new ValidateEnv("en")), x => x.Code == "items.tooMany");
        Assert.Contains(_kind.Validate(Payload(("a", "one"), ("a", "two"), ("c", "three"), ("d", "four")), new ValidateEnv("en")), x => x.Code == "items.duplicateId");
        Assert.Contains(_kind.Validate(Payload(("a", "one"), ("b", " ONE "), ("c", "three"), ("d", "four")), new ValidateEnv("en")), x => x.Code == "items.duplicateText");
        Assert.Contains(_kind.Validate(Payload((" ", "one"), ("b", "two"), ("c", "three"), ("d", "four")), new ValidateEnv("en")), x => x.Code == "items.blankId");
        Assert.Contains(_kind.Validate(Payload(("a", " "), ("b", "two"), ("c", "three"), ("d", "four")), new ValidateEnv("en")), x => x.Code == "items.blankText");
    }

    [Fact]
    public void Cards_are_id_derived_and_reveals_keep_the_authored_correct_order()
    {
        var first = Payload(("d", "Four"), ("b", "Two"), ("a", "One"), ("c", "Three"));
        var second = Payload(("a", "One"), ("b", "Two"), ("c", "Three"), ("d", "Four"));

        for (var seed = 0; seed < 1_000; seed++)
        {
            var context = new ResolveContext($"order-{seed}", seed, []);
            var firstItem = new Item<OrderPayload, OrderResolved>(first, _kind.Resolve(first, context));
            var secondItem = new Item<OrderPayload, OrderResolved>(second, _kind.Resolve(second, new ResolveContext($"order-{seed}", seed, [])));

            Assert.Equal(Json(firstItem.Resolved), Json(secondItem.Resolved));
            Assert.Equal(Json(_kind.Card(firstItem)), Json(_kind.Card(secondItem)));
            Assert.Equal(new[] { "a", "b", "c", "d" }, _kind.Card(firstItem).Items.OrderBy(item => item.Id).Select(item => item.Id));
            Assert.Equal(new[] { "One", "Two", "Three", "Four" }, _kind.Card(firstItem).Items.OrderBy(item => item.Id).Select(item => item.Text));
        }

        Assert.Equal(new[] { "d", "b", "a", "c" }, _kind.Reveal(new(first, _kind.Resolve(first, new ResolveContext("reveal", 0, [])))).ItemIds);
    }

    [Fact]
    public void Runtime_normalizes_placed_ids_and_grades_only_the_correct_sequence()
    {
        IKindRuntime runtime = _kind;
        var payload = Json("{\"items\":[{\"id\":\"d\",\"text\":\"Four\"},{\"id\":\"b\",\"text\":\"Two\"},{\"id\":\"a\",\"text\":\"One\"},{\"id\":\"c\",\"text\":\"Three\"}]}");
        var resolved = runtime.Resolve(payload, new ResolveContext("session", 0, []));

        Assert.Equal("{\"itemIds\":[\"d\",\"b\",\"a\",\"c\"]}", runtime.Normalize(payload, resolved, Json("{\"itemIds\":[\"d\",\"b\",\"a\",\"c\"]}"))?.GetRawText());
        Assert.Equal(1, runtime.Grade(payload, resolved, Json("{\"itemIds\":[\"d\",\"b\",\"a\",\"c\"]}")).Credit);
        Assert.Equal(0, runtime.Grade(payload, resolved, Json("{\"itemIds\":[\"a\",\"b\",\"c\",\"d\"]}")).Credit);
        Assert.Null(runtime.Normalize(payload, resolved, null));
        Assert.Equal(0, runtime.Grade(payload, resolved, null).Credit);

        foreach (var malformed in new[]
        {
            "{\"itemIds\":[\"d\",\"d\",\"a\",\"c\"]}", "{\"itemIds\":[\"d\",\"b\",\"a\"]}",
            "{\"itemIds\":[\"d\",\"b\",\"a\",\"foreign\"]}", "{}", "{\"itemIds\":null}", "{\"itemIds\":\"d,b,a,c\"}"
        })
            Assert.Throws<MalformedAnswer>(() => runtime.Normalize(payload, resolved, Json(malformed)));
    }

    [Fact]
    public void Leaving_the_display_arrangement_unchanged_is_a_valid_placed_answer()
    {
        var payload = Payload(("d", "Four"), ("b", "Two"), ("a", "One"), ("c", "Three"));
        var item = new Item<OrderPayload, OrderResolved>(payload, _kind.Resolve(payload, new ResolveContext("display", 3, [])));
        var displayedIds = _kind.Card(item).Items.Select(value => value.Id!).ToArray();

        var stored = _kind.Normalize(item, new OrderAnswer(displayedIds));

        Assert.Equal(displayedIds, stored.ItemIds);
        var correctIds = payload.Items!.Select(value => value.Id!).ToArray();
        var expectedCredit = displayedIds.SequenceEqual(correctIds, StringComparer.Ordinal) ? 1d : 0d;
        Assert.Equal(expectedCredit, _kind.Grade(item, stored).Credit);
    }

    private static OrderPayload Payload(params (string Id, string Text)[] items) => new(items.Select(item => new OrderItem(item.Id, item.Text)).ToArray());
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
}
