using Quesshi.Features.AiGeneration;

namespace Quesshi.Features.Tests.AiGeneration;

public sealed class UsageReaderTests
{
    [Fact]
    public void Reads_actual_tokens_and_usd_cost_from_a_usage_block()
    {
        var usage = UsageReader.Read("""{"usage":{"prompt_tokens":1200,"completion_tokens":340,"cost":0.00184}}""");

        Assert.Equal(new AiUsage(1200, 340, 0.00184m), usage);
    }

    [Fact]
    public void Missing_usage_fields_are_zero()
    {
        var usage = UsageReader.Read("""{"usage":{"prompt_tokens":5}}""");

        Assert.Equal(new AiUsage(5, 0, 0m), usage);
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("not json")]
    public void Missing_or_invalid_usage_is_not_recorded(string payload)
        => Assert.Null(UsageReader.Read(payload));
}
