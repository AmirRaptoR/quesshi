using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// One line standing in for the whole question form the home used to render before any action: how
/// long, how hard, on what. Issue #87's audit counted thirty-seven controls and six helper sentences
/// ahead of the first tap, every one of them asked again on every duel; this is what replaces them —
/// a summary of the last choice, with a way to change it.
///
/// A summary, not text: the shape is decided here, purely, so "no levels picked reads as Mixed" and
/// "all five picked also reads as Mixed" are provable without a Translator or a rendered page, and
/// the wording lives one layer up in <see cref="DuelSettingsLine"/>. Difficulty is described as a
/// range because that is what issue #89's two-handle slider will produce; a set with holes in it
/// (which today's any-subset chips can still make) is described by its ends, which is the honest
/// approximation rather than a list nobody can read at a glance.
/// </summary>
public sealed record DuelSettingsSummary(int QuestionCount, int? LevelFrom, int? LevelTo, int TopicCount)
{
    /// <summary>Mirrors <c>Quesshi.Domain.Difficulty</c>, the same way DuelSettingsPicker's own list does.</summary>
    public const int LevelCount = 5;

    /// <summary>Nothing was narrowed — the whole ramp, which is also what the server draws from when
    /// the list arrives empty.</summary>
    public bool MixedDifficulty => LevelFrom is null;

    /// <summary>Exactly one level, which reads as its own name rather than as a range of one.</summary>
    public bool OneLevel => LevelFrom is not null && LevelFrom == LevelTo;

    /// <summary>Nothing picked means "surprise me" — the server draws the topics itself.</summary>
    public bool AllTopics => TopicCount == 0;

    public static DuelSettingsSummary From(DuelSettingsDto settings)
    {
        // Out-of-range values are dropped rather than trusted: this list crosses the wire, and a level
        // outside 1..5 has no name to render and would only ever widen the range wrongly.
        var levels = (settings.Levels ?? []).Where(l => l is >= 1 and <= LevelCount).Distinct().Order().ToList();

        // Every level picked says exactly what no level picked says, so both read as Mixed — offering
        // "Very easy–Very hard" as if it were a narrowing would be a distinction without a difference.
        var everything = levels.Count is 0 or LevelCount;

        return new(settings.QuestionCount,
            everything ? null : levels[0],
            everything ? null : levels[^1],
            settings.CategoryIds?.Count ?? 0);
    }
}

/// <summary>
/// The wording of the line above. Deliberately not part of <see cref="DuelSettingsSummary"/>: this
/// half needs a <see cref="Translator"/> and so can only be exercised with the real i18n tables
/// loaded, whereas the half that actually decides anything is pure and is the half under test. Both
/// the home and the placeholder settings page render through here, so the two can never drift.
/// </summary>
public static class DuelSettingsLine
{
    /// <summary>Middle dots because the three parts are peers, not a sentence — "10 questions · Mixed
    /// · 3 topics" is read as three facts, in whichever order the reading direction puts them.</summary>
    public static string Text(DuelSettingsSummary summary, Translator l)
        => string.Join(" · ", Questions(summary, l), Difficulty(summary, l), Topics(summary, l));

    private static string Questions(DuelSettingsSummary s, Translator l)
        => l.Format("home.settingsQuestions", l.Num(s.QuestionCount));

    private static string Difficulty(DuelSettingsSummary s, Translator l)
        => s.MixedDifficulty ? l["home.settingsMixed"]
            : s.OneLevel ? l[$"level.{s.LevelFrom}"]
            // An en dash, not a hyphen: this is a span between two named levels, and it needs no
            // direction handling of its own — the two names swap with the text, the dash stays between.
            : $"{l[$"level.{s.LevelFrom}"]}–{l[$"level.{s.LevelTo}"]}";

    private static string Topics(DuelSettingsSummary s, Translator l)
        => s.AllTopics ? l["home.settingsAllTopics"] : l.Format("home.settingsTopics", l.Num(s.TopicCount));
}
