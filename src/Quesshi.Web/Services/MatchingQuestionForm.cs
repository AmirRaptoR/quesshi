using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>Client-side state and validation for a matching question.</summary>
public sealed class MatchingQuestionForm
{
    public const int MinChoices = 2;
    public const int MaxChoices = 8;

    private bool _useParticipants;

    public string? Id { get; set; }
    public string Lang { get; set; } = "fa";
    public string MatchingCategoryId { get; set; } = "";
    public string Prompt { get; set; } = "";
    public List<string> Choices { get; set; } = ["", ""];
    public string? Subject { get; set; }
    public string? Aspect { get; set; }
    public string? MediaKind { get; set; }
    public string? MediaUrl { get; set; }
    public string? MediaAttribution { get; set; }
    public string Status { get; set; } = "pending";

    /// <summary>Whether answers are the participants in the match instead of authored choices.</summary>
    public bool UseParticipants
    {
        get => _useParticipants;
        set
        {
            if (_useParticipants == value) return;
            _useParticipants = value;
            if (value) Choices.Clear();
            else if (Choices.Count == 0) Choices = ["", ""];
        }
    }

    // Friendly aliases make the state explicit at call sites and preserve a simple binding surface.
    public bool UseParticipantNames { get => UseParticipants; set => UseParticipants = value; }
    public string AnswerSource
    {
        get => UseParticipants ? "participants" : "fixed";
        set => UseParticipants = string.Equals(value?.Trim(), "participants", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsEditing => !string.IsNullOrWhiteSpace(Id);

    public void AddChoice()
    {
        if (!UseParticipants && Choices.Count < MaxChoices) Choices.Add("");
    }

    public void RemoveChoice(int index)
    {
        if (!UseParticipants && Choices.Count > MinChoices && index >= 0 && index < Choices.Count)
            Choices.RemoveAt(index);
    }

    /// <summary>Returns translation keys, one for each client-side rule that is currently broken.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Prompt)) errors.Add("blank_prompt");
        if (string.IsNullOrWhiteSpace(MatchingCategoryId)) errors.Add("unknown_category");

        if (!UseParticipants)
        {
            if (Choices.Count < MinChoices) errors.Add("too_few_choices");
            if (Choices.Count > MaxChoices) errors.Add("too_many_choices");
            if (Choices.Any(string.IsNullOrWhiteSpace)) errors.Add("blank_choice");
            if (Choices.Select(c => c.Trim().ToLowerInvariant()).Distinct().Count() != Choices.Count)
                errors.Add("duplicate_choice");
        }

        return errors;
    }

    public bool TryToDto(out SaveMatchingQuestionDto dto, out IReadOnlyList<string> errors)
    {
        errors = Validate();
        dto = ToDto();
        return errors.Count == 0;
    }

    public SaveMatchingQuestionDto ToDto() => new(
        Id,
        Lang.Trim(),
        MatchingCategoryId.Trim(),
        Prompt.Trim(),
        AnswerSource,
        UseParticipants ? [] : [.. Choices.Select(c => c.Trim())],
        string.IsNullOrWhiteSpace(MediaKind) ? null : MediaKind,
        string.IsNullOrWhiteSpace(MediaUrl) ? null : MediaUrl,
        string.IsNullOrWhiteSpace(MediaAttribution) ? null : MediaAttribution,
        string.IsNullOrWhiteSpace(Subject) ? null : Subject.Trim(),
        string.IsNullOrWhiteSpace(Aspect) ? null : Aspect.Trim(),
        Status);

    public static MatchingQuestionForm From(MatchingQuestionDto question) => new()
    {
        Id = question.Id,
        Lang = question.Lang,
        MatchingCategoryId = question.MatchingCategoryId,
        Prompt = question.Prompt,
        _useParticipants = string.Equals(question.AnswerSource, "participants", StringComparison.OrdinalIgnoreCase),
        Choices = question.AnswerSource.Equals("participants", StringComparison.OrdinalIgnoreCase)
            ? [] : question.Choices.Count is >= MinChoices and <= MaxChoices ? [.. question.Choices] : ["", ""],
        Subject = question.Topic,
        MediaKind = question.Media?.Kind,
        MediaUrl = question.Media?.Url,
        Status = question.Status
    };
}
