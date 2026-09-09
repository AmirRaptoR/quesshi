using Quesshi.Domain;

namespace Quesshi.Infrastructure.Generation;

/// <summary>
/// The shape a batch of sorting questions must come back in.
/// <para>
/// It is the choice schema with one field's meaning changed and one removed: <c>choices</c> is now
/// the items <i>in their correct order</i>, and there is no <c>correctIndex</c> at all, because a
/// sorting question's answer is the order itself and an index would be a second answer with nothing
/// tying it to the first. The pipeline pins the stored index at zero for exactly the same reason.
/// </para>
/// <para>
/// What this schema cannot express is the important part: that the ordering criterion is objective.
/// No schema can. See <c>QuestionPromptBuilder.Sort</c> for what is done about that instead.
/// </para>
/// </summary>
public static class SortSchema
{
    public static object ResponseFormat => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "quesshi_sort_questions",
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
                            required = new[] { "prompt", "choices", "explanation", "subject", "aspect" },
                            properties = new
                            {
                                prompt = new
                                {
                                    type = "string",
                                    description = "The question, naming the measurable criterion and its direction"
                                },
                                choices = new
                                {
                                    type = "array",
                                    description = "The items in their CORRECT order, first to last",
                                    items = new { type = "string" },
                                    minItems = MatchRules.ChoicesPerQuestion,
                                    maxItems = MatchRules.ChoicesPerQuestion
                                },
                                explanation = new
                                {
                                    type = "string",
                                    description = "One sentence giving the values that decide the order"
                                },
                                subject = new { type = "string" },

                                // The aspect is doing double duty for this kind: it is half of the
                                // de-duplication key, as it is everywhere, and it is the ordering
                                // criterion itself. Asking for one field rather than two keeps the
                                // model from naming one criterion in the prompt and another here.
                                aspect = new
                                {
                                    type = "string",
                                    description = "The measurable criterion, one or two words: population, founding year, elevation"
                                }
                            }
                        }
                    }
                }
            }
        }
    };
}
