namespace Quesshi.Web.Services;

/// <summary>What to ask the model for, bound to the generate panel in the admin question list.</summary>
public sealed class AskForm
{
    public string Lang { get; set; } = "fa";
    public string CategoryId { get; set; } = "";
    public int Level { get; set; } = 1;
    public int Count { get; set; } = 5;

    /// <summary>Which kind to ask the model for: <c>"choice"</c>, <c>"sort"</c> or <c>"map"</c>.
    /// One batch is one kind, because each kind is a different prompt and a different set of ways a
    /// candidate can come back wrong — and an admin asking for sorts wants to review sorts.</summary>
    public string Kind { get; set; } = "choice";
}
