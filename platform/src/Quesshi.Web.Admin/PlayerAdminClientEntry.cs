using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

/// <summary>Contributes the tenant player operations screen.</summary>
public sealed class PlayerAdminClientEntry : IAdminClientAssemblyEntry
{
    private static readonly IReadOnlySet<string> ModeratorRoles = new HashSet<string>(["Owner", "Moderator"], StringComparer.OrdinalIgnoreCase);

    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminPlayers));
        registry.RegisterNavigation(new AdminNavigation("/admin/players", PlayerAdminText.Navigation, ModeratorRoles));
        registry.RegisterTranslation(new ClientTranslation("en", new Dictionary<string, string>
        {
            ["admin.players.nav"] = "Players", ["admin.players.title"] = "Players", ["admin.players.denied"] = "Player operations are available to moderators and owners.", ["admin.players.search"] = "Search", ["admin.players.empty"] = "No players found.", ["admin.players.paging"] = "Player pages", ["admin.players.previous"] = "Previous", ["admin.players.next"] = "Next", ["admin.players.active"] = "Active", ["admin.players.banned"] = "Banned", ["admin.players.ban"] = "Ban", ["admin.players.unban"] = "Unban", ["admin.players.loadFailed"] = "Players could not be loaded.", ["admin.players.mutationFailed"] = "The player was not changed."
        }));
        registry.RegisterTranslation(new ClientTranslation("fa", new Dictionary<string, string>
        {
            ["admin.players.nav"] = "بازیکنان", ["admin.players.title"] = "بازیکنان", ["admin.players.denied"] = "عملیات بازیکنان برای مدیران و مالکان در دسترس است.", ["admin.players.search"] = "جستجو", ["admin.players.empty"] = "بازیکنی پیدا نشد.", ["admin.players.paging"] = "صفحه‌های بازیکن", ["admin.players.previous"] = "قبلی", ["admin.players.next"] = "بعدی", ["admin.players.active"] = "فعال", ["admin.players.banned"] = "مسدود", ["admin.players.ban"] = "مسدود کردن", ["admin.players.unban"] = "رفع مسدودیت", ["admin.players.loadFailed"] = "بازیکنان بارگیری نشدند.", ["admin.players.mutationFailed"] = "بازیکن تغییر نکرد."
        }));
    }
}

internal static class PlayerAdminText
{
    public const string Navigation = "admin.players.nav";
    public const string Title = "admin.players.title";
    public const string Denied = "admin.players.denied";
    public const string Search = "admin.players.search";
    public const string Empty = "admin.players.empty";
    public const string Paging = "admin.players.paging";
    public const string Previous = "admin.players.previous";
    public const string Next = "admin.players.next";
    public const string Active = "admin.players.active";
    public const string Banned = "admin.players.banned";
    public const string Ban = "admin.players.ban";
    public const string Unban = "admin.players.unban";
    public const string LoadFailed = "admin.players.loadFailed";
    public const string MutationFailed = "admin.players.mutationFailed";
}
