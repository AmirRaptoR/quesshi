namespace Quesshi.Shared;

public sealed record AnswerResultDto(bool Correct, int CorrectIndex, int Score, string? Explanation, bool RunFinished, int RunScore);
