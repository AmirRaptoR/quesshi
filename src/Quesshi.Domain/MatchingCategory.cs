namespace Quesshi.Domain;

/// <summary>A matching topic. Distinct from <see cref="Category"/> — matching content is a separate
/// aggregate from trivia, and its categories don't mix with trivia's either.</summary>
public sealed record MatchingCategory(string Id, string NameFa, string NameEn, string Icon, string Color,
    bool IsActive = true, int SortOrder = 0, string NameNl = "")
{
    /// <summary>
    /// Falls back to English rather than showing a blank chip: a category added before a language
    /// existed still has to be nameable in it. Mirrors <see cref="Category.NameFor"/>.
    /// </summary>
    public string NameFor(Language lang) => lang switch
    {
        Language.Fa => Or(NameFa, NameEn),
        Language.Nl => Or(NameNl, NameEn),
        _ => Or(NameEn, NameFa)
    };

    private static string Or(string first, string second) => first.Length > 0 ? first : second;
}
