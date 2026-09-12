using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>
/// Turns what the admin form posted into the kind, target and base layer <c>Question.Create</c> and
/// <c>Question.Edit</c> take — or into one error code saying which rule it broke.
///
/// <para>
/// <b>Why a code and not the exception's message.</b> The domain's messages are good English
/// sentences, and this app is played and administered in three languages. A form that showed
/// <c>ex.Message</c> would be showing untranslatable English to a Persian admin, and a form that
/// tried to match on the text would break the day somebody improved a sentence. So every rejection
/// leaves here as a stable identifier — <c>unknown_country</c>, <c>bad_radius</c> — that the panel
/// looks up in its own translation files. The identifiers are the rows of the validation table in
/// <c>docs/sorting-and-map-questions.md</c>, one for one, which is also what makes them testable:
/// there is a test per row asserting the exact code, not merely a 400.
/// </para>
///
/// <para>
/// <b>Why the checks are here as well as in the domain.</b> They are not the same checks.
/// <c>Question.Validate</c> is the authority and runs last, unconditionally, with the map's real
/// country set — nothing gets stored without it. What this adds is <i>which</i> rule failed:
/// "a map question has no choices" and "a question needs exactly four choices" both arrive from the
/// domain as an <c>ArgumentException</c> about <c>choices</c>, and an admin staring at a form needs
/// to be told which of those two things they did.
/// </para>
/// </summary>
public static class QuestionSaveBinding
{
    /// <summary>
    /// Binds and validates. Returns null on success, having set the three out parameters; otherwise
    /// returns the error code and leaves them at their defaults.
    /// </summary>
    public static string? TryBind(SaveQuestionDto body, out QuestionKind kind, out MapTarget? target, out MapBaseLayer? baseLayer)
    {
        kind = QuestionKind.Choice;
        target = null;
        baseLayer = null;

        if (!TryParseKind(body.Kind, out kind)) return "bad_kind";

        var error = kind switch
        {
            QuestionKind.Map => BindMap(body, out target, out baseLayer),
            QuestionKind.Players => BindPlayers(body),
            _ => BindChoiceOrSort(body)
        };

        if (error is not null) return error;

        // The domain has the last word, always, and with the map's own code set in hand. Anything it
        // objects to that the specific checks above did not catch comes back as the general code for
        // whichever field it names.
        try
        {
            Question.Validate(body.Prompt, body.Choices, body.CorrectIndex, kind, target, baseLayer, WorldMapCountries.Codes);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            target = null;
            baseLayer = null;

            return ((ArgumentException)ex).ParamName switch
            {
                "prompt" => "bad_prompt",
                "choices" => "bad_choices",
                "correctIndex" => "bad_correct_index",
                "target" => "bad_target",
                "baseLayer" => "bad_base_layer",
                _ => "bad_question"
            };
        }
    }

    /// <summary>
    /// The two kinds that carry choices. Everything about their choices is the domain's business;
    /// what is checked here is the half of the validation table that says what they must <i>not</i>
    /// have — a stray map target on a choice question is the case the table exists for.
    /// </summary>
    private static string? BindChoiceOrSort(SaveQuestionDto body)
    {
        if (body.Target is not null) return "stray_target";
        if (!string.IsNullOrWhiteSpace(body.BaseLayer)) return "stray_base_layer";

        return null;
    }

    /// <summary>A players question's options are the match's own participants, so choices or a map
    /// target left over from trying it as another kind would be a stray answer nothing reads.</summary>
    private static string? BindPlayers(SaveQuestionDto body)
    {
        if (body.Choices.Count != 0) return "players_has_choices";
        if (body.Target is not null) return "stray_target";
        if (!string.IsNullOrWhiteSpace(body.BaseLayer)) return "stray_base_layer";

        return null;
    }

    private static string? BindMap(SaveQuestionDto body, out MapTarget? target, out MapBaseLayer? baseLayer)
    {
        target = null;
        baseLayer = null;

        // A map question's answer is its target, so choices would be a second answer with no rule
        // tying the two together. Distinguished from "wrong number of choices" because an admin who
        // switched a question's kind and left four options behind needs to be told that, not told to
        // count.
        if (body.Choices.Count != 0) return "map_has_choices";
        if (body.Target is not { } dto) return "target_required";

        if (string.IsNullOrWhiteSpace(body.BaseLayer)) return "base_layer_required";
        if (!Enum.TryParse<MapBaseLayer>(body.BaseLayer, ignoreCase: true, out var layer)) return "bad_base_layer";

        var error = dto.Shape?.Trim().ToLowerInvariant() switch
        {
            "country" => BindCountry(dto, out target),
            "city" => BindCity(dto, out target),
            _ => "bad_target_shape"
        };

        if (error is not null) return error;

        baseLayer = layer;
        return null;
    }

    private static string? BindCountry(MapTargetDto dto, out MapTarget? target)
    {
        target = null;

        var code = dto.CountryCode?.Trim().ToUpperInvariant() ?? "";
        if (code.Length != 2 || !code.All(char.IsAsciiLetterUpper)) return "bad_country_code";

        // The set the bundled SVG can actually draw, which is a fact about a data file rather than
        // about ISO 3166 — "SU" is a real code and not a country this map has a path for.
        if (!WorldMapCountries.Codes.Contains(code)) return "unknown_country";

        target = MapTarget.Country(code);
        return null;
    }

    private static string? BindCity(MapTargetDto dto, out MapTarget? target)
    {
        target = null;

        // Missing, NaN, infinite and out of range all land here as one code, because they are one
        // mistake from the form's point of view: the coordinate is not a place. NaN is the one worth
        // naming — it passes a lazy range check and then makes every distance comparison false, which
        // is a question nobody can ever answer with nothing anywhere reporting a fault.
        if (dto.Latitude is not { } latitude || !Geo.IsValidLatitude(latitude)) return "bad_latitude";
        if (dto.Longitude is not { } longitude || !Geo.IsValidLongitude(longitude)) return "bad_longitude";

        if (dto.RadiusKm is not { } radiusKm || !double.IsFinite(radiusKm)
            || radiusKm < MapTarget.MinRadiusKm || radiusKm > MapTarget.MaxRadiusKm)
            return "bad_radius";

        target = MapTarget.City(latitude, longitude, radiusKm);
        return null;
    }

    /// <summary>
    /// The kind as the wire spells it. An unrecognised string is refused rather than quietly read as
    /// <see cref="QuestionKind.Choice"/>: a typo that silently changed a map question into a choice
    /// one would then fail the choices rule and report something that has nothing to do with what
    /// went wrong.
    /// </summary>
    public static bool TryParseKind(string? value, out QuestionKind kind)
    {
        kind = QuestionKind.Choice;
        return string.IsNullOrWhiteSpace(value) || Enum.TryParse(value, ignoreCase: true, out kind);
    }
}
