namespace Quesshi.Shared;

public sealed record AiSpendDto(long Calls, long PromptTokens, long CompletionTokens, decimal Cost);
