using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Registers the standalone operations dashboard without changing the shared admin shell.</summary>
public sealed class AdminDashboardClientEntry : IAdminClientAssemblyEntry
{
    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminDashboard));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            [AdminDashboardText.Title] = "Dashboard",
            [AdminDashboardText.Players] = "Players",
            [AdminDashboardText.SessionStatuses] = "Sessions by status",
            [AdminDashboardText.SessionFormats] = "Sessions by format",
            [AdminDashboardText.QuestionStatuses] = "Questions by status",
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            [AdminDashboardText.Title] = "داشبورد",
            [AdminDashboardText.Players] = "بازیکنان",
            [AdminDashboardText.SessionStatuses] = "بازی‌ها بر پایهٔ وضعیت",
            [AdminDashboardText.SessionFormats] = "بازی‌ها بر پایهٔ قالب",
            [AdminDashboardText.QuestionStatuses] = "پرسش‌ها بر پایهٔ وضعیت",
        }));
    }
}

internal static class AdminDashboardText
{
    public const string Title = "admin.dashboard.title";
    public const string Players = "admin.dashboard.players";
    public const string SessionStatuses = "admin.dashboard.sessionStatuses";
    public const string SessionFormats = "admin.dashboard.sessionFormats";
    public const string QuestionStatuses = "admin.dashboard.questionStatuses";
}
