using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Contributes tenant language administration without changing the administration shell.</summary>
public sealed class LanguageAdminClientEntry : IAdminClientAssemblyEntry
{
    private static readonly IReadOnlySet<string> OwnerRoles = new HashSet<string>(["Owner"], StringComparer.OrdinalIgnoreCase);

    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminLanguages));
        registry.RegisterNavigation(new AdminNavigation("/admin/languages", LanguageAdminText.Navigation, OwnerRoles));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            ["admin.languages.nav"] = "Languages",
            ["admin.languages.title"] = "Languages",
            ["admin.languages.denied"] = "Language administration is available to tenant owners.",
            ["admin.languages.create"] = "Add language",
            ["admin.languages.save"] = "Save language",
            ["admin.languages.setDefault"] = "Make default",
            ["admin.languages.default"] = "Default language",
            ["admin.languages.remove"] = "Remove language",
            ["admin.languages.code"] = "Language code",
            ["admin.languages.localName"] = "Local display name",
            ["admin.languages.englishName"] = "English name",
            ["admin.languages.direction"] = "Text direction",
            ["admin.languages.ltr"] = "Left to right",
            ["admin.languages.rtl"] = "Right to left",
            ["admin.languages.loadFailed"] = "Languages could not be loaded.",
            ["admin.languages.saveFailed"] = "Languages could not be saved.",
            ["invalid_language"] = "Enter a code, both names, and a valid direction.",
            ["language_taken"] = "That language code is already offered.",
            ["default_language_cannot_be_removed"] = "Choose another default language before removing this one.",
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            ["admin.languages.nav"] = "زبان‌ها",
            ["admin.languages.title"] = "زبان‌ها",
            ["admin.languages.denied"] = "مدیریت زبان‌ها برای مالکان در دسترس است.",
            ["admin.languages.create"] = "افزودن زبان",
            ["admin.languages.save"] = "ذخیره زبان",
            ["admin.languages.setDefault"] = "پیش‌فرض کردن",
            ["admin.languages.default"] = "زبان پیش‌فرض",
            ["admin.languages.remove"] = "حذف زبان",
            ["admin.languages.code"] = "کد زبان",
            ["admin.languages.localName"] = "نام محلی",
            ["admin.languages.englishName"] = "نام انگلیسی",
            ["admin.languages.direction"] = "جهت متن",
            ["admin.languages.ltr"] = "چپ به راست",
            ["admin.languages.rtl"] = "راست به چپ",
            ["admin.languages.loadFailed"] = "زبان‌ها بارگیری نشدند.",
            ["admin.languages.saveFailed"] = "زبان‌ها ذخیره نشدند.",
            ["invalid_language"] = "کد، هر دو نام و جهت معتبر وارد کنید.",
            ["language_taken"] = "این کد زبان از پیش ارائه شده است.",
            ["default_language_cannot_be_removed"] = "پیش از حذف این زبان، زبان پیش‌فرض دیگری انتخاب کنید.",
        }));
    }
}

internal static class LanguageAdminText
{
    public const string Navigation = "admin.languages.nav";
    public const string Title = "admin.languages.title";
    public const string Denied = "admin.languages.denied";
    public const string Create = "admin.languages.create";
    public const string Save = "admin.languages.save";
    public const string SetDefault = "admin.languages.setDefault";
    public const string Default = "admin.languages.default";
    public const string Remove = "admin.languages.remove";
    public const string Code = "admin.languages.code";
    public const string LocalName = "admin.languages.localName";
    public const string EnglishName = "admin.languages.englishName";
    public const string Direction = "admin.languages.direction";
    public const string Ltr = "admin.languages.ltr";
    public const string Rtl = "admin.languages.rtl";
    public const string LoadFailed = "admin.languages.loadFailed";
    public const string SaveFailed = "admin.languages.saveFailed";
}
