namespace Quesshi.Domain;

/// <summary>
/// Which world map a map question is played on. A labelled layer would need a localised
/// country-name dataset in three languages and is deliberately deferred, so there are two.
/// </summary>
public enum MapBaseLayer
{
    /// <summary>Landmasses only. Harder: the player has to know where the borders are.</summary>
    Blank = 0,

    /// <summary>Landmasses with country borders drawn.</summary>
    Borders = 1
}
