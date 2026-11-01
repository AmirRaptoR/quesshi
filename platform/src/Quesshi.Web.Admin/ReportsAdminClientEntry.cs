using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Contributes the reported-questions moderation screen through the role-aware admin page slot.</summary>
public sealed class ReportsAdminClientEntry : IAdminClientAssemblyEntry
{
    private static readonly IReadOnlySet<string> ModeratorRoles = new HashSet<string>(["Owner", "Editor", "Moderator"], StringComparer.OrdinalIgnoreCase);

    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterFeaturePage(new AdminPageContribution("reports", ReportedText.Navigation, typeof(Reported), ModeratorRoles, new AdminCapability("feature", "reports")));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            [ReportedText.Navigation] = "Reported",
            [ReportedText.Title] = "Reported questions",
            [ReportedText.Empty] = "No questions are reported.",
            [ReportedText.Count] = "Reports",
            [ReportedText.Reasons] = "Reasons",
            [ReportedText.Dismiss] = "Dismiss",
            [ReportedText.LoadFailed] = "Reported questions could not be loaded.",
            [ReportedText.DismissFailed] = "The reports could not be dismissed.",
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            [ReportedText.Navigation] = "گزارش‌شده‌ها",
            [ReportedText.Title] = "پرسش‌های گزارش‌شده",
            [ReportedText.Empty] = "پرسشی گزارش نشده است.",
            [ReportedText.Count] = "گزارش‌ها",
            [ReportedText.Reasons] = "دلایل",
            [ReportedText.Dismiss] = "رد گزارش",
            [ReportedText.LoadFailed] = "بارگیری پرسش‌های گزارش‌شده انجام نشد.",
            [ReportedText.DismissFailed] = "رد گزارش‌ها انجام نشد.",
        }));
    }
}

internal static class ReportedText
{
    public const string Navigation = "admin.reported.nav";
    public const string Title = "admin.reported.title";
    public const string Empty = "admin.reported.empty";
    public const string Count = "admin.reported.count";
    public const string Reasons = "admin.reported.reasons";
    public const string Dismiss = "admin.reported.dismiss";
    public const string LoadFailed = "admin.reported.loadFailed";
    public const string DismissFailed = "admin.reported.dismissFailed";
}
