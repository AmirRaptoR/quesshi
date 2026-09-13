namespace Quesshi.Application.UseCases;

public sealed class MatchingGenerationOptions
{
    public int MaxBatchSize { get; set; } = 20;

    /// <summary>
    /// Matching prompts can be socially sensitive in a way factual trivia is not, so new
    /// installations review generated content by default. Operators may opt into direct publishing.
    /// </summary>
    public bool AutoApprove { get; set; }
}
