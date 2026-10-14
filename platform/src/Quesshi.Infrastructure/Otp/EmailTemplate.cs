using System.Net;
using System.Text;

namespace Quesshi.Infrastructure.Otp;

/// <summary>Shared, self-contained email markup that works in restrictive mail clients.</summary>
public static class EmailTemplate
{
    private const string Night = "#090E22";
    private const string Lapis = "#16225C";
    private const string Turquoise = "#2FBFB0";
    private const string Saffron = "#E8A83A";
    private const string Pearl = "#EDE3D0";
    private const string Muted = "#A9A08E";
    private const string Font = "'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif";

    public static string Html(string title, string intro, string hero, string note, bool rtl)
    {
        var dir = rtl ? "rtl" : "ltr";
        var align = rtl ? "right" : "left";
        return $"""
        <!DOCTYPE html>
        <html lang="{(rtl ? "fa" : "en")}" dir="{dir}">
        <head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" /><title>{Escape(title)}</title></head>
        <body style="margin:0;padding:0;background-color:{Night};">
          <div style="display:none;max-height:0;overflow:hidden;opacity:0;">{Escape(intro)}</div>
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background-color:{Night};padding:32px 12px;"><tr><td align="center">
            <table role="presentation" width="600" cellpadding="0" cellspacing="0" border="0" style="width:600px;max-width:100%;background-color:{Lapis};border-radius:16px;">
              <tr><td style="height:4px;background-color:{Turquoise};font-size:0;line-height:0;">&nbsp;</td></tr>
              <tr><td dir="{dir}" align="{align}" style="padding:28px 32px 0;font-family:{Font};"><div style="font-size:13px;letter-spacing:2px;text-transform:uppercase;color:{Turquoise};">Quesshi</div><h1 style="margin:12px 0 0;font-size:22px;line-height:1.3;font-weight:700;color:{Pearl};">{Escape(title)}</h1><p style="margin:12px 0 0;font-size:15px;line-height:1.6;color:{Pearl};">{Escape(intro)}</p></td></tr>
              <tr><td align="center" style="padding:24px 32px 0;">{hero}</td></tr>
              <tr><td dir="{dir}" align="{align}" style="padding:20px 32px 28px;font-family:{Font};"><p style="margin:0;font-size:13px;line-height:1.6;color:{Muted};">{Escape(note)}</p></td></tr>
              <tr><td dir="{dir}" align="{align}" style="padding:16px 32px;background-color:{Night};font-family:{Font};font-size:12px;color:{Muted};">Quesshi · کوئیشی</td></tr>
            </table>
          </td></tr></table>
        </body>
        </html>
        """;
    }

    public static string Code(string code) => $"""
        <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:0 auto;"><tr><td align="center" dir="ltr" style="padding:14px 28px;background-color:{Night};border:1px solid {Saffron};border-radius:12px;font-family:'SF Mono',Menlo,Consolas,monospace;font-size:32px;font-weight:700;letter-spacing:8px;color:{Saffron};">{Escape(code)}</td></tr></table>
        """;

    public static string PlainText(string title, string intro, string hero, string note) => new StringBuilder()
        .AppendLine(title).AppendLine().AppendLine(intro).AppendLine().AppendLine(hero).AppendLine().AppendLine(note).AppendLine().Append("Quesshi").ToString();

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
