namespace Quesshi.Shared;

public sealed record CategoryDto(string Id, string Name, string NameFa, string NameEn, string Icon, string Color,
    bool IsActive, int SortOrder, string NameNl = "");
