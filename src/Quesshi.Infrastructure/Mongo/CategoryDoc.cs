using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

/// <remarks>Extra elements are ignored so a removed field cannot break start-up.</remarks>
[BsonIgnoreExtraElements]
public sealed class CategoryDoc
{
    [BsonId] public string Id { get; set; } = "";
    public string NameFa { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string NameNl { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public static CategoryDoc From(Category c) => new()
    {
        Id = c.Id, NameFa = c.NameFa, NameEn = c.NameEn, NameNl = c.NameNl, Icon = c.Icon, Color = c.Color,
        IsActive = c.IsActive, SortOrder = c.SortOrder
    };

    public Category ToDomain() => new(Id, NameFa, NameEn, Icon, Color, IsActive, SortOrder, NameNl);
}
