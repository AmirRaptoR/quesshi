namespace Quesshi.Server.Seed;

public sealed record SeedCategory(string Id, string NameFa, string NameEn, string Icon, string Color, int SortOrder,
    string NameNl = "");
