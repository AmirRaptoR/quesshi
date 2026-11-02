using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Contributes the owner capability screen without changing the administration shell.</summary>
public sealed class AdminCapabilitiesClientEntry : IAdminClientAssemblyEntry
{
    private static readonly IReadOnlySet<string> OwnerRoles = new HashSet<string>(["Owner"], StringComparer.OrdinalIgnoreCase);

    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminCapabilities));
        registry.RegisterNavigation(new AdminNavigation("/admin/capabilities", CapabilitiesAdminText.Navigation, OwnerRoles));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            [CapabilitiesAdminText.Navigation] = "Capabilities",
            [CapabilitiesAdminText.Title] = "Capabilities",
            [CapabilitiesAdminText.Denied] = "Capabilities are available to tenant owners.",
            [CapabilitiesAdminText.Enabled] = "Enabled",
            [CapabilitiesAdminText.NotEntitled] = "This capability is not part of your plan.",
            [CapabilitiesAdminText.Save] = "Save",
            [CapabilitiesAdminText.Saved] = "Saved.",
            [CapabilitiesAdminText.LoadFailed] = "Capabilities could not be loaded.",
            [CapabilitiesAdminText.SaveFailed] = "The capability could not be saved.",
            [CapabilitiesAdminText.SessionsCapacity] = "Lobby capacity",
            [CapabilitiesAdminText.GoogleClientId] = "Google client id",
            [CapabilitiesAdminText.BankCategoriesLink] = "Manage categories",
            [CapabilitiesAdminText.Name("switch", "guests")] = "Guest players",
            [CapabilitiesAdminText.Name("switch", "custom_questions")] = "Custom questions",
            [CapabilitiesAdminText.Name("switch", "sessions")] = "Sessions",
            [CapabilitiesAdminText.Name("source", "bank")] = "Question bank",
            [CapabilitiesAdminText.Name("source", "pack")] = "Packs",
            [CapabilitiesAdminText.Name("signin", "google")] = "Google sign-in",
            [CapabilitiesAdminText.Name("feature", "friends")] = "Friends",
            [CapabilitiesAdminText.Name("feature", "leaderboard")] = "Leaderboard",
            [CapabilitiesAdminText.Name("feature", "matchmaking")] = "Matchmaking",
            [CapabilitiesAdminText.Name("feature", "reports")] = "Reports",
            [CapabilitiesAdminText.Name("feature", "stats")] = "Stats",
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            [CapabilitiesAdminText.Navigation] = "قابلیت‌ها",
            [CapabilitiesAdminText.Title] = "قابلیت‌ها",
            [CapabilitiesAdminText.Denied] = "قابلیت‌ها برای مالکان در دسترس است.",
            [CapabilitiesAdminText.Enabled] = "فعال",
            [CapabilitiesAdminText.NotEntitled] = "این قابلیت در طرح شما نیست.",
            [CapabilitiesAdminText.Save] = "ذخیره",
            [CapabilitiesAdminText.Saved] = "ذخیره شد.",
            [CapabilitiesAdminText.LoadFailed] = "قابلیت‌ها بارگیری نشدند.",
            [CapabilitiesAdminText.SaveFailed] = "قابلیت ذخیره نشد.",
            [CapabilitiesAdminText.SessionsCapacity] = "ظرفیت لابی",
            [CapabilitiesAdminText.GoogleClientId] = "شناسه کلاینت گوگل",
            [CapabilitiesAdminText.BankCategoriesLink] = "مدیریت دسته‌ها",
        }));
    }
}

internal static class CapabilitiesAdminText
{
    public const string Navigation = "admin.capabilities.nav";
    public const string Title = "admin.capabilities.title";
    public const string Denied = "admin.capabilities.denied";
    public const string Enabled = "admin.capabilities.enabled";
    public const string NotEntitled = "admin.capabilities.notEntitled";
    public const string Save = "admin.capabilities.save";
    public const string Saved = "admin.capabilities.saved";
    public const string LoadFailed = "admin.capabilities.loadFailed";
    public const string SaveFailed = "admin.capabilities.saveFailed";
    public const string SessionsCapacity = "admin.capabilities.sessions.capacity";
    public const string GoogleClientId = "admin.capabilities.google.clientId";
    public const string BankCategoriesLink = "admin.capabilities.bank.categoriesLink";

    public static string Name(string type, string key) => $"admin.capabilities.name.{type}.{key}";
}

/// <summary>Maps each registered capability to its own named form; there is deliberately no generic settings editor.</summary>
public static class CapabilityForms
{
    private static readonly IReadOnlyDictionary<(string Type, string Key), Type> Named = new Dictionary<(string, string), Type>
    {
        [("switch", "guests")] = typeof(GuestsCapabilityForm),
        [("switch", "custom_questions")] = typeof(CustomQuestionsCapabilityForm),
        [("switch", "sessions")] = typeof(SessionsCapabilityForm),
        [("source", "bank")] = typeof(BankSourceCapabilityForm),
        [("source", "pack")] = typeof(PackSourceCapabilityForm),
        [("signin", "google")] = typeof(GoogleSignInCapabilityForm),
        [("feature", "friends")] = typeof(FeatureCapabilityForm),
        [("feature", "leaderboard")] = typeof(FeatureCapabilityForm),
        [("feature", "matchmaking")] = typeof(FeatureCapabilityForm),
        [("feature", "reports")] = typeof(FeatureCapabilityForm),
        [("feature", "stats")] = typeof(FeatureCapabilityForm),
    };

    /// <summary>The form component for a capability, or <see langword="null"/> when no screen owns it yet.</summary>
    public static Type? For(string type, string key) =>
        string.Equals(type, "format", StringComparison.OrdinalIgnoreCase) ? typeof(FormatCapabilityForm)
        : Named.GetValueOrDefault((type.ToLowerInvariant(), key));
}
