using Quesshi.Infrastructure.Generation;

namespace Quesshi.Server.Tests;

public class UsageReaderTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_tokens_and_cost_from_a_usage_block()
    {
        var call = UsageReader.Read(
            """{"model":"google/gemini-2.5-flash","usage":{"prompt_tokens":1200,"completion_tokens":340,"cost":0.00184}}""",
            "id", At, "asked-for-model", "generate");

        Assert.NotNull(call);
        Assert.Equal(1200, call.PromptTokens);
        Assert.Equal(340, call.CompletionTokens);
        Assert.Equal(0.00184m, call.Cost);

        // The model that actually served the request is the one worth recording.
        Assert.Equal("google/gemini-2.5-flash", call.Model);
        Assert.Equal("generate", call.Purpose);
    }

    [Fact]
    public void Falls_back_to_the_requested_model_when_the_response_omits_it()
        => Assert.Equal("asked-for-model",
            UsageReader.Read("""{"usage":{"prompt_tokens":5}}""", "id", At, "asked-for-model", "duplicates")!.Model);

    [Fact]
    public void Missing_fields_count_as_zero_rather_than_failing()
    {
        var call = UsageReader.Read("""{"usage":{"prompt_tokens":5}}""", "id", At, "m", "generate");

        Assert.NotNull(call);
        Assert.Equal(5, call.PromptTokens);
        Assert.Equal(0, call.CompletionTokens);
        Assert.Equal(0m, call.Cost);
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]          // a provider that reports no usage at all
    [InlineData("not json")]
    public void No_usage_means_nothing_to_record(string payload)
        => Assert.Null(UsageReader.Read(payload, "id", At, "m", "generate"));
}
