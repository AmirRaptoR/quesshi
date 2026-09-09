namespace Quesshi.Domain;

/// <summary>
/// What shape a question is. <see cref="Choice"/> is deliberately zero: every question written
/// before this enum existed is a multiple-choice one, so the default value <i>is</i> the existing
/// data and there is no migration and no backfill. Persistence still maps the kind explicitly
/// rather than leaning on that coincidence — see the note in the spec — but the domain default is
/// what makes every current construction path stay valid unchanged.
/// </summary>
public enum QuestionKind
{
    Choice = 0,

    /// <summary>Put the four items in the right order. They are stored in that order and served
    /// shuffled by <see cref="SortOrder"/>, so no separate field holds the answer.</summary>
    Sort = 1,

    /// <summary>Find a place on the world map: a country, or a city within a tolerance radius.</summary>
    Map = 2
}
