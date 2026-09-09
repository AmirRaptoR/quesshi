using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>
/// How much stock one (language, category, level, kind) bucket holds. <see cref="Kind"/> defaults
/// to <see cref="QuestionKind.Choice"/> so every caller written before sorting and map questions
/// existed — including every fake <c>IQuestionRepository</c> in the test suite — keeps compiling
/// and keeps meaning what it meant: a bucket with no kind of its own is a choice bucket.
/// </summary>
public sealed record BucketCount(Language Lang, string CategoryId, Difficulty Level, int Approved, int Pending,
    QuestionKind Kind = QuestionKind.Choice);
