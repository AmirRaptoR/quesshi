using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Contributes the unified five-kind question screen without adding a separate poll route.</summary>
public sealed class QuestionAdminClientEntry : IAdminClientAssemblyEntry
{
    private static readonly IReadOnlySet<string> EditorRoles = new HashSet<string>(["Owner", "Editor"], StringComparer.OrdinalIgnoreCase);

    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminQuestions));
        registry.RegisterNavigation(new AdminNavigation("/admin/questions", QuestionAdminText.Navigation, EditorRoles));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            ["admin.questions.nav"] = "Questions", ["admin.questions.title"] = "Questions", ["admin.questions.denied"] = "Question editing is available to editors and owners.", ["admin.questions.mutationFailed"] = "The question was not changed.",
            ["admin.questions.filters"] = "Filters", ["admin.questions.create"] = "Create question", ["admin.questions.edit"] = "Edit", ["admin.questions.search"] = "Search", ["admin.questions.language"] = "Language", ["admin.questions.family"] = "Family", ["admin.questions.kind"] = "Kind", ["admin.questions.category"] = "Category", ["admin.questions.level"] = "Level", ["admin.questions.status"] = "Status", ["admin.questions.all"] = "All", ["admin.questions.trivia"] = "Trivia", ["admin.questions.poll"] = "Poll", ["admin.questions.pending"] = "Pending", ["admin.questions.approved"] = "Approved", ["admin.questions.rejected"] = "Rejected", ["admin.questions.empty"] = "No questions found.", ["admin.questions.paging"] = "Question pages", ["admin.questions.previous"] = "Previous", ["admin.questions.next"] = "Next", ["admin.questions.loadFailed"] = "Questions could not be loaded."
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            ["admin.questions.nav"] = "پرسش‌ها", ["admin.questions.title"] = "پرسش‌ها", ["admin.questions.denied"] = "ویرایش پرسش‌ها برای ویراستاران و مالکان در دسترس است.", ["admin.questions.mutationFailed"] = "پرسش تغییر نکرد.",
            ["admin.questions.filters"] = "فیلترها", ["admin.questions.create"] = "ایجاد پرسش", ["admin.questions.edit"] = "ویرایش", ["admin.questions.search"] = "جستجو", ["admin.questions.language"] = "زبان", ["admin.questions.family"] = "خانواده", ["admin.questions.kind"] = "نوع", ["admin.questions.category"] = "دسته‌بندی", ["admin.questions.level"] = "سطح", ["admin.questions.status"] = "وضعیت", ["admin.questions.all"] = "همه", ["admin.questions.trivia"] = "دانستنی", ["admin.questions.poll"] = "نظرسنجی", ["admin.questions.pending"] = "در انتظار", ["admin.questions.approved"] = "تأییدشده", ["admin.questions.rejected"] = "ردشده", ["admin.questions.empty"] = "پرسشی پیدا نشد.", ["admin.questions.paging"] = "صفحه‌های پرسش", ["admin.questions.previous"] = "قبلی", ["admin.questions.next"] = "بعدی", ["admin.questions.loadFailed"] = "پرسش‌ها بارگیری نشدند."
        }));
    }
}

internal static class QuestionAdminText
{
    public const string Navigation = "admin.questions.nav";
    public const string Title = "admin.questions.title";
    public const string Denied = "admin.questions.denied";
    public const string MutationFailed = "admin.questions.mutationFailed";
    public const string Filters = "admin.questions.filters";
    public const string Create = "admin.questions.create";
    public const string Edit = "admin.questions.edit";
    public const string Search = "admin.questions.search";
    public const string Language = "admin.questions.language";
    public const string Family = "admin.questions.family";
    public const string Kind = "admin.questions.kind";
    public const string Category = "admin.questions.category";
    public const string Level = "admin.questions.level";
    public const string Status = "admin.questions.status";
    public const string All = "admin.questions.all";
    public const string Trivia = "admin.questions.trivia";
    public const string Poll = "admin.questions.poll";
    public const string Pending = "admin.questions.pending";
    public const string Approved = "admin.questions.approved";
    public const string Rejected = "admin.questions.rejected";
    public const string Empty = "admin.questions.empty";
    public const string Paging = "admin.questions.paging";
    public const string Previous = "admin.questions.previous";
    public const string Next = "admin.questions.next";
    public const string LoadFailed = "admin.questions.loadFailed";
}
