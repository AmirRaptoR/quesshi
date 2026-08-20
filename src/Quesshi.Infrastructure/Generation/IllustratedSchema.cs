using Quesshi.Domain;

namespace Quesshi.Infrastructure.Generation;

/// <summary>Same as the plain schema plus the Wikipedia subject whose picture the question is about.</summary>
public static class IllustratedSchema
{
    public static object ResponseFormat => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "quesshi_illustrated_questions",
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
                            required = new[] { "prompt", "choices", "correctIndex", "explanation", "subject" },
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
                                subject = new
                                {
                                    type = "string",
                                    description = "Exact English Wikipedia article title of the correct answer, whose lead image illustrates the question"
                                }
                            }
                        }
                    }
                }
            }
        }
    };
}
