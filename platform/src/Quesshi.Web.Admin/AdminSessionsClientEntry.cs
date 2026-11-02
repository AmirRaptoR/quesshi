using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Contributes moderator active-session operations without changing other admin children.</summary>
public sealed class AdminSessionsClientEntry : IAdminClientAssemblyEntry
{
    private static readonly IReadOnlySet<string> ModeratorRoles = new HashSet<string>(["Owner", "Moderator"], StringComparer.OrdinalIgnoreCase);

    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminSessions));
        registry.RegisterNavigation(new AdminNavigation("/admin/sessions", AdminSessionsText.Navigation, ModeratorRoles));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            [AdminSessionsText.Navigation] = "Active sessions",
            [AdminSessionsText.Title] = "Active sessions",
            [AdminSessionsText.Denied] = "Session operations are available to moderators.",
            [AdminSessionsText.Empty] = "There are no active sessions.",
            [AdminSessionsText.Format] = "Format",
            [AdminSessionsText.Status] = "Status",
            [AdminSessionsText.Players] = "Players",
            [AdminSessionsText.Created] = "Created",
            [AdminSessionsText.End] = "End session",
            [AdminSessionsText.LoadFailed] = "Active sessions could not be loaded.",
            [AdminSessionsText.EndFailed] = "The session could not be ended.",
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            [AdminSessionsText.Navigation] = "نشست‌های فعال",
            [AdminSessionsText.Title] = "نشست‌های فعال",
            [AdminSessionsText.Denied] = "عملیات نشست برای ناظران در دسترس است.",
            [AdminSessionsText.Empty] = "نشست فعالی وجود ندارد.",
            [AdminSessionsText.Format] = "قالب",
            [AdminSessionsText.Status] = "وضعیت",
            [AdminSessionsText.Players] = "بازیکنان",
            [AdminSessionsText.Created] = "زمان ایجاد",
            [AdminSessionsText.End] = "پایان نشست",
            [AdminSessionsText.LoadFailed] = "بارگیری نشست‌های فعال انجام نشد.",
            [AdminSessionsText.EndFailed] = "پایان دادن به نشست انجام نشد.",
        }));
    }
}

internal static class AdminSessionsText
{
    public const string Navigation = "admin.sessions.nav";
    public const string Title = "admin.sessions.title";
    public const string Denied = "admin.sessions.denied";
    public const string Empty = "admin.sessions.empty";
    public const string Format = "admin.sessions.format";
    public const string Status = "admin.sessions.status";
    public const string Players = "admin.sessions.players";
    public const string Created = "admin.sessions.created";
    public const string End = "admin.sessions.end";
    public const string LoadFailed = "admin.sessions.loadFailed";
    public const string EndFailed = "admin.sessions.endFailed";
}
