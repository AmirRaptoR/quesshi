using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Registers the editor-owned question import screen.</summary>
public sealed class ImportClientEntry : IAdminClientAssemblyEntry
{
    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminImport));
        registry.RegisterNavigation(new AdminNavigation(
            "/admin/import",
            "admin.import.navigation",
            new HashSet<string>(["Owner", "Editor"], StringComparer.OrdinalIgnoreCase)));
        registry.RegisterTranslation(new ClientTranslation("en", English));
        registry.RegisterTranslation(new ClientTranslation("fa", Persian));
    }

    private static IReadOnlyDictionary<string, string> English { get; } = new Dictionary<string, string>
    {
        ["admin.import.navigation"] = "Import questions",
        ["admin.import.title"] = "Import questions",
        ["admin.import.denied"] = "You cannot import questions.",
        ["admin.import.kind"] = "Question kind",
        ["admin.import.file"] = "Question file",
        ["admin.import.bundle"] = "Media bundle (optional)",
        ["admin.import.templates"] = "Templates",
        ["admin.import.csv"] = "CSV",
        ["admin.import.json"] = "JSON",
        ["admin.import.results"] = "Import results",
        ["admin.import.row"] = "Row",
        ["admin.import.outcome"] = "Outcome",
        ["admin.import.problem"] = "Problem",
        ["admin.import.questionId"] = "Question ID",
        ["admin.import.existingQuestionId"] = "Existing question ID",
        ["admin.import.dryRunRequired"] = "Run a dry run for the current inputs before importing.",
        ["admin.import.failed"] = "The import could not be completed.",
        ["admin.import.kind.choice"] = "Choice",
        ["admin.import.kind.order"] = "Order",
        ["admin.import.kind.map_pick"] = "Map pick",
        ["admin.import.kind.poll_choice"] = "Poll choice",
        ["admin.import.kind.poll_roster"] = "Poll roster",
    };

    private static IReadOnlyDictionary<string, string> Persian { get; } = new Dictionary<string, string>
    {
        ["admin.import.navigation"] = "وارد کردن پرسش‌ها",
        ["admin.import.title"] = "وارد کردن پرسش‌ها",
        ["admin.import.denied"] = "اجازهٔ وارد کردن پرسش‌ها را ندارید.",
        ["admin.import.kind"] = "نوع پرسش",
        ["admin.import.file"] = "پروندهٔ پرسش‌ها",
        ["admin.import.bundle"] = "بستهٔ رسانه (اختیاری)",
        ["admin.import.templates"] = "الگوها",
        ["admin.import.csv"] = "CSV",
        ["admin.import.json"] = "JSON",
        ["admin.import.results"] = "نتیجهٔ وارد کردن",
        ["admin.import.row"] = "ردیف",
        ["admin.import.outcome"] = "نتیجه",
        ["admin.import.problem"] = "مشکل",
        ["admin.import.questionId"] = "شناسهٔ پرسش",
        ["admin.import.existingQuestionId"] = "شناسهٔ پرسش موجود",
        ["admin.import.dryRunRequired"] = "پیش از ثبت نهایی، ورودی‌های کنونی را آزمایشی اجرا کنید.",
        ["admin.import.failed"] = "وارد کردن کامل نشد.",
        ["admin.import.kind.choice"] = "چندگزینه‌ای",
        ["admin.import.kind.order"] = "ترتیب",
        ["admin.import.kind.map_pick"] = "انتخاب روی نقشه",
        ["admin.import.kind.poll_choice"] = "نظرسنجی چندگزینه‌ای",
        ["admin.import.kind.poll_roster"] = "نظرسنجی فهرست",
    };
}

internal enum ImportText
{
    Navigation,
    Title,
    Denied,
    Kind,
    File,
    Bundle,
    Templates,
    Csv,
    Json,
    Results,
    Row,
    Outcome,
    Problem,
    QuestionId,
    ExistingQuestionId,
    DryRunRequired,
    Failed,
    KindChoice,
    KindOrder,
    KindMapPick,
    KindPollChoice,
    KindPollRoster,
}

internal static class ImportCopy
{
    private static readonly IReadOnlyDictionary<ImportText, string> Keys = new Dictionary<ImportText, string>
    {
        [ImportText.Navigation] = "admin.import.navigation",
        [ImportText.Title] = "admin.import.title",
        [ImportText.Denied] = "admin.import.denied",
        [ImportText.Kind] = "admin.import.kind",
        [ImportText.File] = "admin.import.file",
        [ImportText.Bundle] = "admin.import.bundle",
        [ImportText.Templates] = "admin.import.templates",
        [ImportText.Csv] = "admin.import.csv",
        [ImportText.Json] = "admin.import.json",
        [ImportText.Results] = "admin.import.results",
        [ImportText.Row] = "admin.import.row",
        [ImportText.Outcome] = "admin.import.outcome",
        [ImportText.Problem] = "admin.import.problem",
        [ImportText.QuestionId] = "admin.import.questionId",
        [ImportText.ExistingQuestionId] = "admin.import.existingQuestionId",
        [ImportText.DryRunRequired] = "admin.import.dryRunRequired",
        [ImportText.Failed] = "admin.import.failed",
        [ImportText.KindChoice] = "admin.import.kind.choice",
        [ImportText.KindOrder] = "admin.import.kind.order",
        [ImportText.KindMapPick] = "admin.import.kind.map_pick",
        [ImportText.KindPollChoice] = "admin.import.kind.poll_choice",
        [ImportText.KindPollRoster] = "admin.import.kind.poll_roster",
    };

    public static string Get(IClientTranslator translator, ImportText text)
    {
        ArgumentNullException.ThrowIfNull(translator);
        return translator[Keys[text]];
    }
}
