namespace Quesshi.Shared;

/// <summary>
/// A question as the admin form submits it.
/// <para>
/// The three fields at the end are new and all three default, so a body written before sorting and
/// map questions existed still describes exactly the choice question it always did — which is what
/// keeps every existing admin test, and any script anybody has pointed at this endpoint, working
/// unchanged.
/// </para>
/// </summary>
/// <param name="Kind"><c>"choice"</c>, <c>"sort"</c> or <c>"map"</c>.</param>
/// <param name="Target">The map target, for a map question. Must be null for the other two kinds.</param>
/// <param name="BaseLayer"><c>"blank"</c> or <c>"borders"</c>, for a map question. Null otherwise.</param>
public sealed record SaveQuestionDto(string? Id, string Lang, string CategoryId, int Level, string Prompt,
    List<string> Choices, int CorrectIndex, string? Explanation, string? MediaKind, string? MediaUrl, string Status,
    string Kind = "choice", MapTargetDto? Target = null, string? BaseLayer = null);
