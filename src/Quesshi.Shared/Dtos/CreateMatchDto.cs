namespace Quesshi.Shared;

/// <summary>
/// What the challenger asked for. Null categories means "surprise me", null questions means the
/// default length; both are validated server-side because this arrives from a browser.
/// </summary>
public sealed record CreateMatchDto(bool Random = false, string? Lang = null,
    List<string>? Categories = null, int? Questions = null);
