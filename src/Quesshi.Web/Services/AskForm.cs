namespace Quesshi.Web.Services;

/// <summary>What to ask the model for, bound to the generate panel in the admin question list.</summary>
public sealed class AskForm
{
    public string Lang { get; set; } = "fa";
    public string CategoryId { get; set; } = "";
    public int Level { get; set; } = 1;
    public int Count { get; set; } = 5;
}
