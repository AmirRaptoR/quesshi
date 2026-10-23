namespace Quesshi.Plugin.Api;

/// <summary>A player's read-only statistics within one tenant.</summary>
public sealed record StatsPlayerSummary(
    Guid PlayerId,
    IReadOnlyList<StatsFormatSummary> Formats,
    IReadOnlyList<StatsCategorySummary> Categories);

/// <summary>A player's aggregate results for one session format.</summary>
public sealed record StatsFormatSummary(
    string Format,
    int Played,
    int Wins,
    int Losses,
    int Draws,
    long Points,
    int Streak,
    int BestStreak);

/// <summary>A player's aggregate accuracy for one category and its current names.</summary>
public sealed record StatsCategorySummary(
    Guid CategoryId,
    int Asked,
    double CreditSum,
    IReadOnlyDictionary<string, string> Names);

/// <summary>Read-only usage and accuracy totals for one question.</summary>
public sealed record StatsQuestionUsage(Guid QuestionId, long Served, long Asked, double Accuracy);
