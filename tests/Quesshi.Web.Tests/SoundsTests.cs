using Microsoft.JSInterop;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// <see cref="Sounds"/> keeps its mute and autoplay gates in plain C# for exactly this reason: neither
/// needs a browser to exercise. This double stands in for <see cref="IJSRuntime"/> and just records
/// every identifier it was asked to invoke, and returns whatever the test pre-loaded for it — good
/// enough for a class whose entire JS surface is three flat, argument-light calls.
/// </summary>
public class SoundsTests
{
    private sealed class FakeJsRuntime(Dictionary<string, object?>? responses = null) : IJSRuntime
    {
        private readonly Dictionary<string, object?> _responses = responses ?? [];
        public List<string> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(identifier);
            var value = _responses.TryGetValue(identifier, out var v) ? (TValue)v! : default!;
            return ValueTask.FromResult(value);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }

    /// <summary>Settings (#91) writing "0" must stop a cue before it ever reaches the code that would
    /// touch an AudioContext — this checks the mute read happens (it is the very first thing every
    /// call does) and that nothing past it does.</summary>
    [Fact]
    public async Task Disabled_sounds_never_reach_the_play_call()
    {
        var js = new FakeJsRuntime(new Dictionary<string, object?> { ["quesshi.get"] = "0" });

        await new Sounds(js).Correct();

        Assert.Contains("quesshi.get", js.Calls);
        Assert.DoesNotContain("quesshi.sounds.armed", js.Calls);
        Assert.DoesNotContain("quesshi.sounds.play", js.Calls);
    }

    /// <summary>Before the page's first pointerdown/keydown, the JS side reports itself unarmed — no
    /// AudioContext exists yet, and this must never call the one interop function that would touch it.</summary>
    [Fact]
    public async Task Unarmed_sounds_never_reach_the_play_call()
    {
        var js = new FakeJsRuntime(new Dictionary<string, object?> { ["quesshi.get"] = "1", ["quesshi.sounds.armed"] = false });

        await new Sounds(js).Wrong();

        Assert.Contains("quesshi.sounds.armed", js.Calls);
        Assert.DoesNotContain("quesshi.sounds.play", js.Calls);
    }

    /// <summary>Once neither gate objects, the cue actually plays — the counterpart to the two tests
    /// above, so a change that broke both gates by always returning early would still be caught.</summary>
    [Fact]
    public async Task Unmuted_and_armed_plays_the_cue()
    {
        var js = new FakeJsRuntime(new Dictionary<string, object?> { ["quesshi.get"] = null, ["quesshi.sounds.armed"] = true });

        await new Sounds(js).Win();

        Assert.Contains("quesshi.sounds.play", js.Calls);
    }

    /// <summary>A page that calls a cue while the circuit is going away, or before Blazor's JS side has
    /// finished initialising, must never see an exception — a sound effect is never worth a crashed page.</summary>
    [Fact]
    public async Task An_interop_failure_never_throws()
    {
        var sounds = new Sounds(new ThrowingJsRuntime());

        var exception = await Record.ExceptionAsync(() => sounds.Correct());

        Assert.Null(exception);
    }

    private sealed class ThrowingJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("no JS here");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("no JS here");
    }
}
