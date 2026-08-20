using Quesshi.Shared;

namespace Quesshi.Web.Services;

public sealed class CategoryForm
{
    public string Id { get; set; } = "";
    public string NameFa { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string NameNl { get; set; } = "";
    public string Icon { get; set; } = "◆";
    public string Color { get; set; } = "#2EC4B6";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public static CategoryForm From(CategoryDto c) => new()
    {
        Id = c.Id, NameFa = c.NameFa, NameEn = c.NameEn, NameNl = c.NameNl, Icon = c.Icon,
        Color = c.Color, IsActive = c.IsActive, SortOrder = c.SortOrder
    };

    public CategoryDto ToDto() => new(Id.Trim(), NameFa.Trim(), NameFa.Trim(), NameEn.Trim(), Icon, Color, IsActive, SortOrder, NameNl.Trim());
}
