using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>Filters matching questions for authoring and administration.</summary>
public sealed record MatchingQuestionFilter(
    Language? Lang = null, string? CategoryId = null, QuestionStatus? Status = null,
    string? Text = null, int Skip = 0, int Take = 50);
