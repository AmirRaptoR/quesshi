namespace Quesshi.Web.Services;

/// <summary>The common table shape of trivia and matching categories.</summary>
public sealed record AdminCategoryRow(string Id, string NameFa, string NameEn, string NameNl,
    string Icon, string Color, bool IsActive);
