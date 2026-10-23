using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Contributes the tenant audit screen without changing the shared administration shell.</summary>
public sealed class AdminAuditClientEntry : IAdminClientAssemblyEntry
{
    private static readonly IReadOnlySet<string> OwnerRoles = new HashSet<string>(["Owner"], StringComparer.OrdinalIgnoreCase);

    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminAudit));
        registry.RegisterNavigation(new AdminNavigation("/admin/audit", AdminAuditText.Navigation, OwnerRoles));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            [AdminAuditText.Navigation] = "Audit log",
            [AdminAuditText.Title] = "Audit log",
            [AdminAuditText.Denied] = "The audit log is available to owners.",
            [AdminAuditText.Actor] = "Actor",
            [AdminAuditText.Target] = "Target",
            [AdminAuditText.Time] = "Time",
            [AdminAuditText.LoadOlder] = "Load older events",
            [AdminAuditText.LoadFailed] = "The audit log could not be loaded.",
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            [AdminAuditText.Navigation] = "گزارش رویدادها",
            [AdminAuditText.Title] = "گزارش رویدادها",
            [AdminAuditText.Denied] = "گزارش رویدادها برای مالکان در دسترس است.",
            [AdminAuditText.Actor] = "انجام‌دهنده",
            [AdminAuditText.Target] = "هدف",
            [AdminAuditText.Time] = "زمان",
            [AdminAuditText.LoadOlder] = "بارگیری رویدادهای قدیمی‌تر",
            [AdminAuditText.LoadFailed] = "گزارش رویدادها بارگیری نشد.",
        }));
    }
}

internal static class AdminAuditText
{
    public const string Navigation = "admin.audit.nav";
    public const string Title = "admin.audit.title";
    public const string Denied = "admin.audit.denied";
    public const string Actor = "admin.audit.actor";
    public const string Target = "admin.audit.target";
    public const string Time = "admin.audit.time";
    public const string LoadOlder = "admin.audit.loadOlder";
    public const string LoadFailed = "admin.audit.loadFailed";
}
