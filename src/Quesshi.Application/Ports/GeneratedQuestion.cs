namespace Quesshi.Application.Ports;

/// <summary>A question as it comes back from the model, before anything has validated it.</summary>
public sealed record GeneratedQuestion(string Prompt, IReadOnlyList<string> Choices, int CorrectIndex, string? Explanation)
{
    /// <summary>
    /// For illustrated questions: the Wikipedia article naming what the picture should show,
    /// which is always the correct answer. Sourcing the image from the answer is what keeps the
    /// question correct — we never have to trust that a picture depicts what it claims.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>Which property of the subject is being asked — "director", "capital", "year".</summary>
    public string? Aspect { get; init; }
}
