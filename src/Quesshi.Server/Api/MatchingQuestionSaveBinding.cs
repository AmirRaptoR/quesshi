using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>Validates the matching authoring contract and returns stable, translatable error codes.</summary>
public static class MatchingQuestionSaveBinding
{
    public static string? TryBind(SaveMatchingQuestionDto body, out Language lang,
        out MatchingAnswerSource answerSource, out List<string> choices, out MediaRef media,
        out QuestionStatus status, out string? topic)
    {
        lang = Language.Fa;
        answerSource = MatchingAnswerSource.Participants;
        choices = body.Choices ?? [];
        media = MediaRef.None;
        status = QuestionStatus.Pending;
        topic = TopicKey.From(body.Subject, body.Aspect);

        var langValue = body.Lang?.Trim().ToLowerInvariant();
        if (langValue is not ("fa" or "en" or "nl")) return "bad_lang";
        lang = langValue.ToLanguage();

        var sourceValue = body.AnswerSource?.Trim().ToLowerInvariant();
        if (sourceValue is not ("participants" or "fixed")) return "bad_answer_source";
        answerSource = sourceValue == "fixed" ? MatchingAnswerSource.Fixed : MatchingAnswerSource.Participants;

        if (string.IsNullOrWhiteSpace(body.Prompt)) return "blank_prompt";

        if (answerSource == MatchingAnswerSource.Participants)
        {
            if (choices.Count != 0) return "choices_not_allowed";
        }
        else
        {
            if (choices.Count < MatchingRules.MinFixedChoices) return "too_few_choices";
            if (choices.Count > MatchingRules.MaxFixedChoices) return "too_many_choices";
            if (choices.Any(string.IsNullOrWhiteSpace)) return "blank_choice";
            if (choices.Select(c => c.Trim().ToLowerInvariant()).Distinct().Count() != choices.Count)
                return "duplicate_choice";
        }

        var statusValue = body.Status?.Trim();
        if (!Enum.TryParse<QuestionStatus>(statusValue, true, out status) || !Enum.IsDefined(status))
            return "bad_status";

        var rawUrl = body.MediaUrl;
        var rawKind = body.MediaKind;
        // An omitted or genuinely empty pair means no media. Whitespace is intentionally an error:
        // it is almost always a form binding bug and must not be persisted as a URL.
        if (rawUrl is null || rawUrl.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(rawKind)) return "bad_media";
        }
        else if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return "bad_media";
        }
        else
        {
            if (!Enum.TryParse<MediaKind>(rawKind?.Trim(), true, out var mediaKind)
                || !Enum.IsDefined(mediaKind) || mediaKind == MediaKind.None)
                return "bad_media";
            media = new MediaRef(mediaKind, rawUrl.Trim(), body.MediaAttribution);
        }

        // Keep the domain as the final authority. This catches future domain rules and any malformed
        // enum value that slips through the transport-level checks without leaking an exception as 500.
        try
        {
            MatchingQuestion.Validate(body.Prompt, answerSource, choices);
        }
        catch (ArgumentException ex)
        {
            return ex.ParamName switch
            {
                "prompt" => "blank_prompt",
                "choices" when answerSource == MatchingAnswerSource.Participants => "choices_not_allowed",
                "choices" when choices.Count < MatchingRules.MinFixedChoices => "too_few_choices",
                "choices" when choices.Count > MatchingRules.MaxFixedChoices => "too_many_choices",
                "choices" when choices.Any(string.IsNullOrWhiteSpace) => "blank_choice",
                "choices" => "duplicate_choice",
                "answerSource" => "bad_answer_source",
                _ => "bad_matching_question"
            };
        }

        return null;
    }
}
