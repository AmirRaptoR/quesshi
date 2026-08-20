using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public sealed record QuestionFilter(
    Language? Lang = null, string? CategoryId = null, Difficulty? Level = null,
    QuestionStatus? Status = null, string? Text = null, int Skip = 0, int Take = 50,
    /// <summary>True: only questions players have reported, worst first. Null: no opinion.</summary>
    bool? Reported = null);
