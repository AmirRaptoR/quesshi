using Quesshi.Shared;

namespace Quesshi.Web.Services;

public sealed class QuestionForm
{
    public string? Id { get; set; }
    public string Lang { get; set; } = "fa";
    public string CategoryId { get; set; } = "";
    public int Level { get; set; } = 1;
    public string Prompt { get; set; } = "";
    public List<string> Choices { get; set; } = ["", "", "", ""];
    public int CorrectIndex { get; set; }
    public string? Explanation { get; set; }
    public string? MediaKind { get; set; }
    public string? MediaUrl { get; set; }
    public string Status { get; set; } = "pending";

    public static QuestionForm From(AdminQuestionDto q) => new()
    {
        Id = q.Id, Lang = q.Lang, CategoryId = q.CategoryId, Level = q.Level, Prompt = q.Prompt,
        Choices = [.. q.Choices], CorrectIndex = q.CorrectIndex, Explanation = q.Explanation,
        MediaKind = q.Media?.Kind, MediaUrl = q.Media?.Url, Status = q.Status
    };

    public SaveQuestionDto ToDto() => new(Id, Lang, CategoryId, Level, Prompt.Trim(),
        [.. Choices.Select(c => c.Trim())], CorrectIndex, Explanation, MediaKind, MediaUrl, Status);
}
