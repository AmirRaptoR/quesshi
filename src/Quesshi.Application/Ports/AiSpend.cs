namespace Quesshi.Application.Ports;

/// <summary>What a window of <see cref="AiCall"/>s adds up to.</summary>
public sealed record AiSpend(long Calls, long PromptTokens, long CompletionTokens, decimal Cost)
{
    public static readonly AiSpend Nothing = new(0, 0, 0, 0m);

    public long Tokens => PromptTokens + CompletionTokens;
}
