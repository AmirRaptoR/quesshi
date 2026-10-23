using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Contributes owner-only durable-job recovery without changing other admin children.</summary>
public sealed class AdminDeadJobsClientEntry : IAdminClientAssemblyEntry
{
    private static readonly IReadOnlySet<string> OwnerRoles = new HashSet<string>(["Owner"], StringComparer.OrdinalIgnoreCase);

    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminDeadJobs));
        registry.RegisterNavigation(new AdminNavigation("/admin/dead-jobs", AdminDeadJobsText.Navigation, OwnerRoles));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            [AdminDeadJobsText.Navigation] = "Dead jobs",
            [AdminDeadJobsText.Title] = "Dead jobs",
            [AdminDeadJobsText.Denied] = "Dead-job recovery is available to tenant owners.",
            [AdminDeadJobsText.Empty] = "There are no dead jobs.",
            [AdminDeadJobsText.Attempts] = "Attempts",
            [AdminDeadJobsText.LastError] = "Last error",
            [AdminDeadJobsText.Time] = "Time",
            [AdminDeadJobsText.Status] = "Status",
            [AdminDeadJobsText.None] = "None",
            [AdminDeadJobsText.Rerun] = "Rerun",
            [AdminDeadJobsText.Refresh] = "Refresh status",
            [AdminDeadJobsText.LoadFailed] = "Dead jobs could not be loaded.",
            [AdminDeadJobsText.RerunFailed] = "The dead job could not be rerun.",
            [AdminDeadJobsText.RefreshFailed] = "The dead-job status could not be refreshed.",
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            [AdminDeadJobsText.Navigation] = "کارهای متوقف‌شده",
            [AdminDeadJobsText.Title] = "کارهای متوقف‌شده",
            [AdminDeadJobsText.Denied] = "بازیابی کارهای متوقف‌شده برای مالکان مستأجر در دسترس است.",
            [AdminDeadJobsText.Empty] = "کار متوقف‌شده‌ای وجود ندارد.",
            [AdminDeadJobsText.Attempts] = "تلاش‌ها",
            [AdminDeadJobsText.LastError] = "آخرین خطا",
            [AdminDeadJobsText.Time] = "زمان",
            [AdminDeadJobsText.Status] = "وضعیت",
            [AdminDeadJobsText.None] = "ندارد",
            [AdminDeadJobsText.Rerun] = "اجرای دوباره",
            [AdminDeadJobsText.Refresh] = "تازه‌سازی وضعیت",
            [AdminDeadJobsText.LoadFailed] = "بارگیری کارهای متوقف‌شده انجام نشد.",
            [AdminDeadJobsText.RerunFailed] = "اجرای دوبارهٔ کار متوقف‌شده انجام نشد.",
            [AdminDeadJobsText.RefreshFailed] = "تازه‌سازی وضعیت کار متوقف‌شده انجام نشد.",
        }));
    }
}

internal static class AdminDeadJobsText
{
    public const string Navigation = "admin.deadJobs.nav";
    public const string Title = "admin.deadJobs.title";
    public const string Denied = "admin.deadJobs.denied";
    public const string Empty = "admin.deadJobs.empty";
    public const string Attempts = "admin.deadJobs.attempts";
    public const string LastError = "admin.deadJobs.lastError";
    public const string Time = "admin.deadJobs.time";
    public const string Status = "admin.deadJobs.status";
    public const string None = "admin.deadJobs.none";
    public const string Rerun = "admin.deadJobs.rerun";
    public const string Refresh = "admin.deadJobs.refresh";
    public const string LoadFailed = "admin.deadJobs.loadFailed";
    public const string RerunFailed = "admin.deadJobs.rerunFailed";
    public const string RefreshFailed = "admin.deadJobs.refreshFailed";
}
