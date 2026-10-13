using System.Text.Json;
using Quesshi.Kinds.Order;

namespace Quesshi.Kinds.Tests;

public sealed class OrderFixtureTests
{
    [Fact]
    public void Fixtures_cover_runtime_behavior_and_card_noninterference() =>
        KindFixtureRunner.Run("order", new OrderKind(), payload =>
        {
            using var document = JsonDocument.Parse(payload.GetRawText());
            var items = document.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => new OrderItem(item.GetProperty("id").GetString(), item.GetProperty("text").GetString()))
                .Reverse()
                .ToArray();
            return JsonSerializer.SerializeToElement(new OrderPayload(items), OrderJsonContext.Default.OrderPayload);
        });
}
