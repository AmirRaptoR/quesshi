using Quesshi.Domain;

namespace Quesshi.Infrastructure.Generation;

/// <summary>
/// The shape a batch of map questions must come back in: a target that is either a country or a
/// point, never both.
/// <para>
/// <c>countryCode</c> is required for <b>both</b> shapes, and that is the only unobvious thing here.
/// For a country question it is the answer. For a city question it is the country the city sits in,
/// which the question never stores and no player ever sees — it exists so the pipeline can check the
/// coordinates against that country's outline on the real map before believing them. A model that
/// gives Porto's name, Portugal's code and Rome's coordinates is caught by that and by nothing else.
/// </para>
/// <para>
/// The city-only fields are unioned with null rather than omitted, because OpenRouter's strict
/// structured output requires every declared property to be present in <c>required</c>: "absent" is
/// not something the schema can ask for, so "explicitly null" is what a country question sends.
/// </para>
/// </summary>
public static class MapSchema
{
    public static object ResponseFormat => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "quesshi_map_questions",
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
                            required = new[]
                            {
                                "prompt", "targetKind", "countryCode", "latitude", "longitude", "radiusKm",
                                "explanation", "subject", "aspect"
                            },
                            properties = new
                            {
                                prompt = new
                                {
                                    type = "string",
                                    description = "The question. Must not contain the name of the place being asked for."
                                },
                                targetKind = new
                                {
                                    type = "string",
                                    @enum = new[] { "country", "city" },
                                    description = "Whether the answer is a whole country or a point"
                                },
                                countryCode = new
                                {
                                    type = "string",
                                    description = "ISO 3166-1 alpha-2. The answer for a country question; the country the city is in for a city question."
                                },
                                latitude = new
                                {
                                    type = new[] { "number", "null" },
                                    minimum = Geo.MinLatitude,
                                    maximum = Geo.MaxLatitude,
                                    description = "City questions only. Decimal degrees, north positive."
                                },
                                longitude = new
                                {
                                    type = new[] { "number", "null" },
                                    minimum = Geo.MinLongitude,
                                    maximum = Geo.MaxLongitude,
                                    description = "City questions only. Decimal degrees, east positive."
                                },
                                radiusKm = new
                                {
                                    type = new[] { "number", "null" },
                                    minimum = MapTarget.MinRadiusKm,
                                    maximum = MapTarget.MaxRadiusKm,
                                    description = "City questions only. How close a pin has to land: 50 km is hard, 300 km is gentle."
                                },
                                explanation = new
                                {
                                    type = "string",
                                    description = "One sentence naming the place, shown after the round"
                                },
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
