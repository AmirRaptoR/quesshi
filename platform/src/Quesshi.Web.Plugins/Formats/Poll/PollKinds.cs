using System.Text.Json;
using Quesshi.Kinds.PollChoice;
using Quesshi.Kinds.PollRoster;

namespace Quesshi.Web.Plugins.Formats.Poll;

/// <summary>Reads the JSON a poll view carries into the typed values the poll kind components take, and writes their answers back.</summary>
public static class PollKinds
{
    public const string Choice = "poll_choice";
    public const string Roster = "poll_roster";

    public static KindCard? Card(string? kind, int? version, JsonElement? card)
    {
        if (kind is null || version is null || card is not { } value) return null;
        object? typed = Try(() => kind switch
        {
            Choice => value.Deserialize(PollChoiceJsonContext.Default.PollChoiceCard),
            Roster => value.Deserialize(PollRosterJsonContext.Default.PollRosterCard),
            _ => (object?)null
        });
        return typed is null ? null : new KindCard(kind, version.Value, typed);
    }

    /// <summary>The command payload of an answer a poll kind's play component produced.</summary>
    public static JsonElement? Answer(string kind, object? answer) => answer switch
    {
        PollChoiceAnswer choice when kind == Choice => JsonSerializer.SerializeToElement(choice, PollChoiceJsonContext.Default.PollChoiceAnswer),
        PollRosterAnswer roster when kind == Roster => JsonSerializer.SerializeToElement(roster, PollRosterJsonContext.Default.PollRosterAnswer),
        _ => null
    };

    /// <summary>The text key or option text that names a stored answer: an option's text, a seat's name, or a translation key for the fixed answers.</summary>
    public static StoredChoice? Stored(KindCard? card, JsonElement? stored)
    {
        if (card is null || stored is not { ValueKind: JsonValueKind.Object } value) return null;
        return Try(() => card.Value switch
        {
            PollChoiceCard choice when value.Deserialize(PollChoiceJsonContext.Default.PollChoiceStored) is { } answer =>
                answer.NotApplicable == true ? StoredChoice.Key("voting.answer.notApplicable")
                : choice.Options.FirstOrDefault(option => option.Id == answer.Option) is { } option ? StoredChoice.Text(option.Text ?? "") : null,
            PollRosterCard roster when value.Deserialize(PollRosterJsonContext.Default.PollRosterStored) is { } answer =>
                answer.NotApplicable == true ? StoredChoice.Key("voting.answer.notApplicable")
                : answer.Several == true ? StoredChoice.Key("voting.answer.multiplePeople")
                : answer.None == true ? StoredChoice.Key("voting.answer.noOne")
                : roster.Options.FirstOrDefault(option => option.Seat == answer.Seat) is { } option ? StoredChoice.Text(option.Name) : null,
            _ => null
        });
    }

    private static T? Try<T>(Func<T?> read)
    {
        try { return read(); }
        catch (JsonException) { return default; }
    }
}

/// <summary>A stored answer as the screen names it: literal text from the card, or a translation key.</summary>
public sealed record StoredChoice(string Value, bool IsKey)
{
    public static StoredChoice Key(string key) => new(key, true);
    public static StoredChoice Text(string text) => new(text, false);
}
