namespace Quesshi.Shared;

/// <summary>
/// Ask for a batch of questions in one bucket. <see cref="Kind"/> defaults to <c>"choice"</c>, so a
/// request written before sorting and map questions existed still means what it meant.
/// </summary>
public sealed record GenerateRequestDto(string Lang, string CategoryId, int Level, int Count, string Kind = "choice");
