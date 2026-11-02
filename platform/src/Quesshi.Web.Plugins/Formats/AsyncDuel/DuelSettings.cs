namespace Quesshi.Web.Plugins.Formats.AsyncDuel;

/// <summary>What an owner picks for an async duel; an empty category or level list means "no preference".</summary>
public sealed record DuelSettings(int QuestionCount, List<string> CategoryIds, List<int> Levels)
{
    /// <summary>The player's pack to draw from; null draws from the question bank.</summary>
    public string? PackId { get; init; }
}

/// <summary>A pack the owner may pick as the lobby's content.</summary>
public sealed record PickerPack(string Id, string Title);

/// <summary>A category the picker may offer; <c>Langs</c> null means the server did not narrow it by language.</summary>
public sealed record PickerCategory(string Id, string Name, string Icon, string Color, bool IsActive = true, List<string>? Langs = null);
