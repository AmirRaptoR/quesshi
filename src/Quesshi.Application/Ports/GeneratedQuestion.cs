using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>A question as it comes back from the model, before anything has validated it.</summary>
public sealed record GeneratedQuestion(string Prompt, IReadOnlyList<string> Choices, int CorrectIndex, string? Explanation)
{
    /// <summary>
    /// For illustrated questions: the Wikipedia article naming what the picture should show,
    /// which is always the correct answer. Sourcing the image from the answer is what keeps the
    /// question correct — we never have to trust that a picture depicts what it claims.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>Which property of the subject is being asked — "director", "capital", "year".</summary>
    public string? Aspect { get; init; }

    // --- map questions ----------------------------------------------------------------
    //
    // These four carry a map candidate's answer. They are init-only properties with null defaults
    // rather than constructor parameters for the same reason Subject and Aspect are: every existing
    // construction of this record — the choice pipeline, every test — stays valid unchanged, and a
    // choice question that carried a country code would be a candidate nobody wrote.
    //
    // Note what is asked for and what is done with it. For a city the model is asked for the city's
    // country as well as its coordinates, and that country is not stored anywhere: it exists purely
    // so the pipeline can check the two against each other (WorldMapGeometry). It is the one lie a
    // model tells about map questions that nothing in the strings can catch.

    /// <summary>
    /// Which shape of target the model was asked for and answered with. Null on anything that is not
    /// a map candidate.
    /// </summary>
    public MapTargetKind? TargetShape { get; init; }

    /// <summary>
    /// ISO 3166-1 alpha-2. For a country target it <i>is</i> the answer; for a city target it is the
    /// country the city is claimed to be in, which is what the coordinates get checked against.
    /// </summary>
    public string? CountryCode { get; init; }

    /// <summary>The city's latitude, for a city target.</summary>
    public double? Latitude { get; init; }

    /// <summary>The city's longitude, for a city target.</summary>
    public double? Longitude { get; init; }

    /// <summary>How close a pin has to land, in kilometres — the question's difficulty lever.</summary>
    public double? RadiusKm { get; init; }
}
