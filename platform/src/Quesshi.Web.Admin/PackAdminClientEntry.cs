using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Contributes the tenant-owned pack screen for editors.</summary>
public sealed class PackAdminClientEntry : IAdminClientAssemblyEntry
{
    private static readonly IReadOnlySet<string> EditorRoles = new HashSet<string>(["Owner", "Editor"], StringComparer.OrdinalIgnoreCase);

    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminPacks));
        registry.RegisterNavigation(new AdminNavigation("/admin/packs", PackAdminText.Navigation, EditorRoles));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            [PackAdminText.Navigation] = "Packs", [PackAdminText.Title] = "Packs", [PackAdminText.Denied] = "Pack editing is available to editors and owners.",
            [PackAdminText.New] = "New pack", [PackAdminText.Name] = "Title", [PackAdminText.Description] = "Description", [PackAdminText.Language] = "Language", [PackAdminText.Visibility] = "Visibility",
            [PackAdminText.Private] = "Private", [PackAdminText.Unlisted] = "Unlisted", [PackAdminText.Tenant] = "Whole tenant", [PackAdminText.Create] = "Create", [PackAdminText.Save] = "Save", [PackAdminText.Delete] = "Delete", [PackAdminText.Edit] = "Edit",
            [PackAdminText.Empty] = "No packs yet.", [PackAdminText.Questions] = "Questions", [PackAdminText.NoQuestions] = "This pack has no questions.", [PackAdminText.Prompt] = "Prompt", [PackAdminText.Kind] = "Kind",
            [PackAdminText.AddQuestion] = "Add question", [PackAdminText.Remove] = "Remove", [PackAdminText.MoveUp] = "Move up", [PackAdminText.MoveDown] = "Move down", [PackAdminText.Failed] = "The change was not saved.", [PackAdminText.LoadFailed] = "Packs could not be loaded.",
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            [PackAdminText.Navigation] = "بسته‌ها", [PackAdminText.Title] = "بسته‌ها", [PackAdminText.Denied] = "ویرایش بسته‌ها برای ویراستاران و مالکان در دسترس است.",
            [PackAdminText.New] = "بسته جدید", [PackAdminText.Name] = "عنوان", [PackAdminText.Description] = "توضیح", [PackAdminText.Language] = "زبان", [PackAdminText.Visibility] = "نمایش",
            [PackAdminText.Private] = "خصوصی", [PackAdminText.Unlisted] = "فهرست‌نشده", [PackAdminText.Tenant] = "کل مجموعه", [PackAdminText.Create] = "ایجاد", [PackAdminText.Save] = "ذخیره", [PackAdminText.Delete] = "حذف", [PackAdminText.Edit] = "ویرایش",
            [PackAdminText.Empty] = "هنوز بسته‌ای نیست.", [PackAdminText.Questions] = "پرسش‌ها", [PackAdminText.NoQuestions] = "این بسته پرسشی ندارد.", [PackAdminText.Prompt] = "متن پرسش", [PackAdminText.Kind] = "نوع",
            [PackAdminText.AddQuestion] = "افزودن پرسش", [PackAdminText.Remove] = "برداشتن", [PackAdminText.MoveUp] = "بالا", [PackAdminText.MoveDown] = "پایین", [PackAdminText.Failed] = "تغییر ذخیره نشد.", [PackAdminText.LoadFailed] = "بسته‌ها بارگیری نشدند.",
        }));
    }
}

internal static class PackAdminText
{
    public const string Navigation = "admin.packs.nav";
    public const string Title = "admin.packs.title";
    public const string Denied = "admin.packs.denied";
    public const string New = "admin.packs.new";
    public const string Name = "admin.packs.name";
    public const string Description = "admin.packs.description";
    public const string Language = "admin.packs.language";
    public const string Visibility = "admin.packs.visibility";
    public const string Private = "admin.packs.private";
    public const string Unlisted = "admin.packs.unlisted";
    public const string Tenant = "admin.packs.tenant";
    public const string Create = "admin.packs.create";
    public const string Save = "admin.packs.save";
    public const string Delete = "admin.packs.delete";
    public const string Edit = "admin.packs.edit";
    public const string Empty = "admin.packs.empty";
    public const string Questions = "admin.packs.questions";
    public const string NoQuestions = "admin.packs.noQuestions";
    public const string Prompt = "admin.packs.prompt";
    public const string Kind = "admin.packs.kind";
    public const string AddQuestion = "admin.packs.addQuestion";
    public const string Remove = "admin.packs.remove";
    public const string MoveUp = "admin.packs.moveUp";
    public const string MoveDown = "admin.packs.moveDown";
    public const string Failed = "admin.packs.failed";
    public const string LoadFailed = "admin.packs.loadFailed";
}
