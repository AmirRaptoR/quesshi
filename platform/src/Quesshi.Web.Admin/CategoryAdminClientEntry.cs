using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Contributes the category and bank policy screen without changing the administration shell.</summary>
public sealed class CategoryAdminClientEntry : IAdminClientAssemblyEntry
{
    private static readonly IReadOnlySet<string> EditorRoles = new HashSet<string>(["Owner", "Editor"], StringComparer.OrdinalIgnoreCase);

    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminCategories));
        registry.RegisterNavigation(new AdminNavigation("/admin/categories", CategoryAdminText.Navigation, EditorRoles));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            ["admin.categories.nav"] = "Categories",
            ["admin.categories.title"] = "Categories",
            ["admin.categories.denied"] = "Category editing is available to editors and owners.",
            ["admin.categories.create"] = "Create category",
            ["admin.categories.save"] = "Save category",
            ["admin.categories.bankSettings"] = "Bank category policy",
            ["admin.categories.family.trivia"] = "Trivia",
            ["admin.categories.family.poll"] = "Poll",
            ["admin.categories.saveBankSettings"] = "Save bank category policy",
            ["admin.categories.slug"] = "Identifier",
            ["admin.categories.icon"] = "Icon",
            ["admin.categories.color"] = "Colour",
            ["admin.categories.order"] = "Order",
            ["admin.categories.active"] = "Active",
            ["admin.categories.loadFailed"] = "Categories could not be loaded.",
            ["admin.categories.saveFailed"] = "Categories could not be saved.",
            ["invalid_bank_category_settings"] = "Choose existing categories for each bank family."
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            ["admin.categories.nav"] = "دسته‌بندی‌ها",
            ["admin.categories.title"] = "دسته‌بندی‌ها",
            ["admin.categories.denied"] = "ویرایش دسته‌بندی‌ها برای ویراستاران و مالکان در دسترس است.",
            ["admin.categories.create"] = "ایجاد دسته‌بندی",
            ["admin.categories.save"] = "ذخیره دسته‌بندی",
            ["admin.categories.bankSettings"] = "سیاست دسته‌بندی بانک",
            ["admin.categories.family.trivia"] = "دانستنی",
            ["admin.categories.family.poll"] = "نظرسنجی",
            ["admin.categories.saveBankSettings"] = "ذخیره سیاست بانک",
            ["admin.categories.slug"] = "شناسه",
            ["admin.categories.icon"] = "آیکن",
            ["admin.categories.color"] = "رنگ",
            ["admin.categories.order"] = "ترتیب",
            ["admin.categories.active"] = "فعال",
            ["admin.categories.loadFailed"] = "دسته‌بندی‌ها بارگیری نشدند.",
            ["admin.categories.saveFailed"] = "دسته‌بندی‌ها ذخیره نشدند.",
            ["invalid_bank_category_settings"] = "برای هر خانواده بانک، دسته‌بندی‌های موجود را انتخاب کنید."
        }));
    }
}

internal static class CategoryAdminText
{
    public const string Navigation = "admin.categories.nav";
    public const string Title = "admin.categories.title";
    public const string Denied = "admin.categories.denied";
    public const string Create = "admin.categories.create";
    public const string Save = "admin.categories.save";
    public const string BankSettings = "admin.categories.bankSettings";
    public const string SaveBankSettings = "admin.categories.saveBankSettings";
    public const string Slug = "admin.categories.slug";
    public const string Icon = "admin.categories.icon";
    public const string Color = "admin.categories.color";
    public const string Order = "admin.categories.order";
    public const string Active = "admin.categories.active";
    public const string LoadFailed = "admin.categories.loadFailed";
    public const string SaveFailed = "admin.categories.saveFailed";

    public static string Family(string family) => $"admin.categories.family.{family}";
}
