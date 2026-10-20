using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

public sealed class MediaClientEntry : IAdminClientAssemblyEntry
{
    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterPage(typeof(AdminMedia));
        registry.RegisterNavigation(new AdminNavigation("/admin/media", "admin.media.nav", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "editor", "owner" }));
        registry.RegisterTranslation(new ClientTranslation("en", English));
        registry.RegisterTranslation(new ClientTranslation("fa", Persian));
        registry.RegisterTranslation(new ClientTranslation("nl", Dutch));
    }

    private static IReadOnlyDictionary<string, string> English { get; } = new Dictionary<string, string>
    {
        ["admin.media.nav"] = "Media",
        ["admin.media.title"] = "Question media",
        ["admin.media.attribution"] = "Attribution",
        ["admin.media.upload"] = "Upload image, audio, or video",
        ["admin.media.url"] = "Media URL",
        ["admin.media.fetch"] = "Fetch media",
        ["admin.media.selected"] = "Selected asset ID",
        ["admin.media.invalid_media_url"] = "Enter an http or https URL.",
        ["admin.media.media_destination_forbidden"] = "That address cannot be fetched.",
        ["admin.media.media_too_large"] = "The media file is too large.",
        ["admin.media.unsupported_media"] = "That media type is not supported.",
        ["admin.media.media_type_mismatch"] = "The media bytes do not match its type.",
        ["admin.media.media_redirect_limit"] = "The media URL redirected too many times.",
        ["admin.media.media_fetch_timeout"] = "Fetching the media timed out.",
        ["admin.media.media_fetch_failed"] = "The media could not be fetched.",
        ["admin.media.invalid_media"] = "The upload is not valid media.",
        ["admin.media.error"] = "The media could not be saved.",
    };

    private static IReadOnlyDictionary<string, string> Persian { get; } = new Dictionary<string, string>
    {
        ["admin.media.nav"] = "رسانه",
        ["admin.media.title"] = "رسانهٔ پرسش",
        ["admin.media.attribution"] = "ذکر منبع",
        ["admin.media.upload"] = "بارگذاری تصویر، صدا یا ویدیو",
        ["admin.media.url"] = "نشانی رسانه",
        ["admin.media.fetch"] = "دریافت رسانه",
        ["admin.media.selected"] = "شناسهٔ دارایی انتخاب‌شده",
        ["admin.media.invalid_media_url"] = "یک نشانی http یا https وارد کنید.",
        ["admin.media.media_destination_forbidden"] = "این نشانی قابل دریافت نیست.",
        ["admin.media.media_too_large"] = "پروندهٔ رسانه بسیار بزرگ است.",
        ["admin.media.unsupported_media"] = "این نوع رسانه پشتیبانی نمی‌شود.",
        ["admin.media.media_type_mismatch"] = "دادهٔ رسانه با نوع آن هم‌خوان نیست.",
        ["admin.media.media_redirect_limit"] = "نشانی رسانه بیش از اندازه تغییرمسیر داد.",
        ["admin.media.media_fetch_timeout"] = "زمان دریافت رسانه به پایان رسید.",
        ["admin.media.media_fetch_failed"] = "دریافت رسانه انجام نشد.",
        ["admin.media.invalid_media"] = "بارگذاری رسانهٔ معتبر نیست.",
        ["admin.media.error"] = "رسانه ذخیره نشد.",
    };

    private static IReadOnlyDictionary<string, string> Dutch { get; } = new Dictionary<string, string>
    {
        ["admin.media.nav"] = "Media",
        ["admin.media.title"] = "Vraagmedia",
        ["admin.media.attribution"] = "Naamsvermelding",
        ["admin.media.upload"] = "Afbeelding, audio of video uploaden",
        ["admin.media.url"] = "Media-URL",
        ["admin.media.fetch"] = "Media ophalen",
        ["admin.media.selected"] = "Geselecteerde asset-ID",
        ["admin.media.invalid_media_url"] = "Voer een http- of https-URL in.",
        ["admin.media.media_destination_forbidden"] = "Dat adres kan niet worden opgehaald.",
        ["admin.media.media_too_large"] = "Het mediabestand is te groot.",
        ["admin.media.unsupported_media"] = "Dit mediatype wordt niet ondersteund.",
        ["admin.media.media_type_mismatch"] = "De mediabytes komen niet overeen met het type.",
        ["admin.media.media_redirect_limit"] = "De media-URL heeft te vaak doorgestuurd.",
        ["admin.media.media_fetch_timeout"] = "Het ophalen van de media duurde te lang.",
        ["admin.media.media_fetch_failed"] = "De media kon niet worden opgehaald.",
        ["admin.media.invalid_media"] = "De upload is geen geldig mediabestand.",
        ["admin.media.error"] = "De media kon niet worden opgeslagen.",
    };
}
