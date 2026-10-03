namespace Quesshi.Domain;

/// <summary>
/// Category boundary for content sampling. Null means all categories including uncategorized
/// content; an empty list means no categories; a populated list means only those ids.
/// </summary>
public sealed record ContentScope(IReadOnlyList<string>? CategoryIds)
{
    public static ContentScope All { get; } = new((IReadOnlyList<string>?)null);
}
