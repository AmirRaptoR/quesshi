using Quesshi.Domain;

namespace Quesshi.Infrastructure.Generation;

/// <summary>Strict structured-output shapes for scoreless matching questions.</summary>
public static class MatchingQuestionSchema
{
    public static object ResponseFormat(MatchingAnswerSource answerSource)
        => answerSource == MatchingAnswerSource.Fixed ? Fixed : Participants;

    private static object Participants => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "quesshi_matching_participant_questions",
            strict = true,
            schema = Envelope(new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "prompt", "subject", "aspect" },
                properties = new
                {
                    prompt = new { type = "string" },
                    subject = new { type = "string", description = "2-6 English words naming the specific situation; never a category, translation, or numbered ID" },
                    aspect = new { type = "string", description = "1-4 English words naming the exact comparison dimension; never a numbered ID" }
                }
            })
        }
    };

    private static object Fixed => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "quesshi_matching_fixed_questions",
            strict = true,
            schema = Envelope(new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "prompt", "choices", "subject", "aspect" },
                properties = new
                {
                    prompt = new { type = "string" },
                    choices = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        minItems = MatchingRules.MinFixedChoices,
                        maxItems = MatchingRules.MaxFixedChoices
                    },
                    subject = new { type = "string", description = "2-6 English words naming the specific situation; never a category, translation, or numbered ID" },
                    aspect = new { type = "string", description = "1-4 English words naming the exact answer dimension; never a numbered ID" }
                }
            })
        }
    };

    private static object Envelope(object item) => new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "questions" },
        properties = new
        {
            questions = new
            {
                type = "array",
                items = item
            }
        }
    };
}
