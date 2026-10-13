using System.Text.Json;
using System.Text.Json.Serialization;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.PollChoice;
using Quesshi.Kinds.PollRoster;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class FixtureTests
{
    [Fact]
    public void Choice_fixtures_cover_runtime_behavior_and_card_noninterference() =>
        KindFixtureRunner.Run("choice", new ChoiceKind(), payload =>
        {
            using var document = JsonDocument.Parse(payload.GetRawText());
            var options = document.RootElement.GetProperty("options").EnumerateArray().Select(option => option.GetString()!).ToArray();
            return JsonSerializer.SerializeToElement(new ChoicePayload(options, 0), ChoiceJsonContext.Default.ChoicePayload);
        });

    [Fact]
    public void Poll_choice_fixtures_cover_runtime_behavior() =>
        KindFixtureRunner.Run("poll_choice", new PollChoiceKind(), payload => payload);

    [Fact]
    public void Poll_roster_fixtures_cover_runtime_behavior() =>
        KindFixtureRunner.Run("poll_roster", new PollRosterKind(), payload => payload);

    [Fact]
    public void Fixture_runner_detects_a_card_that_leaks_the_correct_answer()
    {
        Assert.Throws<InvalidOperationException>(() => KindFixtureRunner.Run("choice", new LeakyChoiceKind(), payload =>
        {
            using var document = JsonDocument.Parse(payload.GetRawText());
            return JsonSerializer.SerializeToElement(new ChoicePayload(document.RootElement.GetProperty("options").EnumerateArray().Select(option => option.GetString()!).ToArray(), 0), ChoiceJsonContext.Default.ChoicePayload);
        }, verifyExpected: false));
    }

    private sealed class LeakyChoiceKind : ChoiceKind
    {
        public override ChoiceCard Card(Item<ChoicePayload, ChoiceResolved> item) => new([$"answer:{item.Payload.CorrectIndex}"]);
    }
}
