using Microsoft.JSInterop;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public class SoundsTests
{
    private sealed class FakeJsRuntime(Dictionary<string, object?>? responses = null) : IJSRuntime
    {
        private readonly Dictionary<string, object?> responses = responses ?? [];
        public List<string> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(identifier);
            return ValueTask.FromResult(responses.TryGetValue(identifier, out var value) ? (TValue)value! : default!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    [Fact]
    public async Task Disabled_sounds_never_reach_the_play_call()
    {
        var js = new FakeJsRuntime(new Dictionary<string, object?> { ["quesshi.get"] = "0" });

        await new Sounds(js).Correct();

        Assert.Contains("quesshi.get", js.Calls);
        Assert.DoesNotContain("quesshi.sounds.armed", js.Calls);
        Assert.DoesNotContain("quesshi.sounds.play", js.Calls);
    }

    [Fact]
    public async Task Unarmed_sounds_never_reach_the_play_call()
    {
        var js = new FakeJsRuntime(new Dictionary<string, object?> { ["quesshi.get"] = "1", ["quesshi.sounds.armed"] = false });

        await new Sounds(js).Wrong();

        Assert.Contains("quesshi.sounds.armed", js.Calls);
        Assert.DoesNotContain("quesshi.sounds.play", js.Calls);
    }

    [Fact]
    public async Task Unmuted_and_armed_plays_the_cue()
    {
        var js = new FakeJsRuntime(new Dictionary<string, object?> { ["quesshi.get"] = null, ["quesshi.sounds.armed"] = true });

        await new Sounds(js).Win();

        Assert.Contains("quesshi.sounds.play", js.Calls);
    }

    [Fact]
    public async Task An_interop_failure_never_throws()
    {
        var exception = await Record.ExceptionAsync(() => new Sounds(new ThrowingJsRuntime()).Correct());

        Assert.Null(exception);
    }

    private sealed class ThrowingJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("no JS here");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("no JS here");
    }
}
