using Quesshi.Domain;

namespace Quesshi.Infrastructure.Generation;

/// <summary>The JSON schema the model must answer with, in OpenRouter's structured-output shape.</summary>
public static class QuestionSchema
{
    public static object ResponseFormat => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "quesshi_questions",
            strict = true,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "questions" },
                properties = new
                {
                    questions = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            required = new[] { "prompt", "choices", "correctIndex", "explanation", "subject", "aspect" },
                            properties = new
                            {
                                prompt = new { type = "string" },
                                choices = new
                                {
                                    type = "array",
                                    items = new { type = "string" },
                                    minItems = MatchRules.ChoicesPerQuestion,
                                    maxItems = MatchRules.ChoicesPerQuestion
                                },
                                correctIndex = new { type = "integer", minimum = 0, maximum = MatchRules.ChoicesPerQuestion - 1 },
                                explanation = new { type = "string" },

                                // Together these are the question's identity: two questions with the
                                // same pair are the same question, however differently they are worded.
                                subject = new { type = "string" },
                                aspect = new { type = "string" }
                            }
                        }
                    }
                }
            }
        }
    };
}
