using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Contributes tenant branding administration without changing the administration shell.</summary>
public sealed class BrandingAdminClientEntry : IAdminClientAssemblyEntry
{
    private static readonly IReadOnlySet<string> OwnerRoles = new HashSet<string>(["Owner"], StringComparer.OrdinalIgnoreCase);

    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminBranding));
        registry.RegisterNavigation(new AdminNavigation("/admin/branding", BrandingAdminText.Navigation, OwnerRoles));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            [BrandingAdminText.Navigation] = "Branding",
            [BrandingAdminText.Title] = "Branding",
            [BrandingAdminText.Denied] = "Branding is available to tenant owners.",
            [BrandingAdminText.Theme] = "Theme colours",
            [BrandingAdminText.Logo] = "Logo",
            [BrandingAdminText.Icon] = "App icon",
            [BrandingAdminText.AppName] = "App name",
            [BrandingAdminText.LandingText] = "Landing text",
            [BrandingAdminText.Save] = "Save branding",
            [BrandingAdminText.Saved] = "Branding saved.",
            [BrandingAdminText.LoadFailed] = "Branding could not be loaded.",
            [BrandingAdminText.SaveFailed] = "Branding could not be saved.",
            ["invalid_branding"] = "Enter colours as #RRGGBB and keep texts within their length limits.",
            ["invalid_branding_language"] = "Texts can only be set for languages the tenant offers.",
            ["invalid_branding_media"] = "Choose an image uploaded to this tenant.",
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            [BrandingAdminText.Navigation] = "برندسازی",
            [BrandingAdminText.Title] = "برندسازی",
            [BrandingAdminText.Denied] = "برندسازی برای مالکان در دسترس است.",
            [BrandingAdminText.Theme] = "رنگ‌های پوسته",
            [BrandingAdminText.Logo] = "نشان",
            [BrandingAdminText.Icon] = "نماد برنامه",
            [BrandingAdminText.AppName] = "نام برنامه",
            [BrandingAdminText.LandingText] = "متن صفحه آغازین",
            [BrandingAdminText.Save] = "ذخیره برندسازی",
            [BrandingAdminText.Saved] = "برندسازی ذخیره شد.",
            [BrandingAdminText.LoadFailed] = "برندسازی بارگیری نشد.",
            [BrandingAdminText.SaveFailed] = "برندسازی ذخیره نشد.",
            ["invalid_branding"] = "رنگ‌ها را به شکل ‎#RRGGBB‎ وارد کنید و طول متن‌ها را رعایت کنید.",
            ["invalid_branding_language"] = "متن‌ها فقط برای زبان‌های ارائه‌شده قابل تنظیم‌اند.",
            ["invalid_branding_media"] = "تصویری را که در این مجموعه بارگذاری شده انتخاب کنید.",
        }));
    }
}

internal static class BrandingAdminText
{
    public const string Navigation = "admin.branding.nav";
    public const string Title = "admin.branding.title";
    public const string Denied = "admin.branding.denied";
    public const string Theme = "admin.branding.theme";
    public const string Logo = "admin.branding.logo";
    public const string Icon = "admin.branding.icon";
    public const string AppName = "admin.branding.appName";
    public const string LandingText = "admin.branding.landingText";
    public const string Save = "admin.branding.save";
    public const string Saved = "admin.branding.saved";
    public const string LoadFailed = "admin.branding.loadFailed";
    public const string SaveFailed = "admin.branding.saveFailed";
}
