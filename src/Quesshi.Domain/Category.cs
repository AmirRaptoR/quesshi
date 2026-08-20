namespace Quesshi.Domain;

/// <summary>A topic. Id is a slug so it can live in URLs and grain keys unescaped.</summary>
public sealed record Category(string Id, string NameFa, string NameEn, string Icon, string Color,
    bool IsActive = true, int SortOrder = 0, string NameNl = "")
{
    /// <summary>
    /// Falls back to English rather than showing a blank chip: a category added before a language
    /// existed still has to be nameable in it.
    /// </summary>
    public string NameFor(Language lang) => lang switch
    {
        Language.Fa => Or(NameFa, NameEn),
        Language.Nl => Or(NameNl, NameEn),
        _ => Or(NameEn, NameFa)
    };

    private static string Or(string first, string second) => first.Length > 0 ? first : second;
}
