namespace Quesshi.Shared;

/// <param name="Langs">
/// The language codes this category can actually be played in — one approved question is enough.
/// A category with none is offerable but unplayable, which is how a Persian profile could ask for
/// the Dutch-only KNM bank and be handed ten questions about birds instead.
/// </param>
public sealed record CategoryDto(string Id, string Name, string NameFa, string NameEn, string Icon, string Color,
    bool IsActive, int SortOrder, string NameNl = "", List<string>? Langs = null);
