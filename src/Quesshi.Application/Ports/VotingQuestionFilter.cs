using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>Filters voting questions for authoring and administration.</summary>
public sealed record VotingQuestionFilter(
    Language? Lang = null, string? CategoryId = null, QuestionStatus? Status = null,
    string? Text = null, int Skip = 0, int Take = 50);
