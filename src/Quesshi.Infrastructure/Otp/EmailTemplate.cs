using System.Net;
using System.Text;

namespace Quesshi.Infrastructure.Otp;

/// <summary>
/// The chrome around every message the app sends, in the app's own palette — lapis ground,
/// turquoise rule, saffron for the thing you came for.
///
/// Written for mail clients rather than for browsers, which is a different and worse medium:
/// layout is tables because Outlook renders with Word, every style is inline because most clients
/// strip &lt;style&gt;, the width is fixed at 600px because that is what the narrow preview panes
/// assume, and nothing is loaded from anywhere — no image, no font, no stylesheet — because images
/// are blocked by default and a mail that depends on them arrives blank.
/// </summary>
public static class EmailTemplate
{
    private const string Night = "#090E22";
    private const string Lapis = "#16225C";
    private const string Turquoise = "#2FBFB0";
    private const string Saffron = "#E8A83A";
    private const string Pearl = "#EDE3D0";
    private const string Muted = "#A9A08E";

    /// <summary>Vazirmatn carries both scripts in the app; a mail client will have none of them.</summary>
    private const string Font = "'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif";

    /// <param name="hero">The one thing the message exists to deliver: a code, or a button.</param>
    public static string Html(string title, string intro, string hero, string note, bool rtl)
    {
        var dir = rtl ? "rtl" : "ltr";
        var align = rtl ? "right" : "left";

        return $"""
        <!DOCTYPE html>
        <html lang="{(rtl ? "fa" : "en")}" dir="{dir}">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <meta name="color-scheme" content="dark" />
          <title>{Escape(title)}</title>
        </head>
        <body style="margin:0;padding:0;background-color:{Night};">
          <!-- Shown in the inbox list beside the subject, then hidden. -->
          <div style="display:none;max-height:0;overflow:hidden;opacity:0;">{Escape(intro)}</div>

          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"
                 style="background-color:{Night};padding:32px 12px;">
            <tr>
              <td align="center">
                <table role="presentation" width="600" cellpadding="0" cellspacing="0" border="0"
                       style="width:600px;max-width:100%;background-color:{Lapis};border-radius:16px;">

                  <tr>
                    <td style="height:4px;background-color:{Turquoise};font-size:0;line-height:0;">&nbsp;</td>
                  </tr>

                  <tr>
                    <td dir="{dir}" align="{align}" style="padding:28px 32px 0 32px;font-family:{Font};">
                      <div style="font-size:13px;letter-spacing:2px;text-transform:uppercase;color:{Turquoise};">
                        Quesshi
                      </div>
                      <h1 style="margin:12px 0 0 0;font-size:22px;line-height:1.3;font-weight:700;color:{Pearl};">
                        {Escape(title)}
                      </h1>
                      <p style="margin:12px 0 0 0;font-size:15px;line-height:1.6;color:{Pearl};">
                        {Escape(intro)}
                      </p>
                    </td>
                  </tr>

                  <tr>
                    <td align="center" style="padding:24px 32px 0 32px;">{hero}</td>
                  </tr>

                  <tr>
                    <td dir="{dir}" align="{align}" style="padding:20px 32px 28px 32px;font-family:{Font};">
                      <p style="margin:0;font-size:13px;line-height:1.6;color:{Muted};">{Escape(note)}</p>
                    </td>
                  </tr>

                  <tr>
                    <td dir="{dir}" align="{align}"
                        style="padding:16px 32px;background-color:{Night};font-family:{Font};font-size:12px;color:{Muted};">
                      Quesshi · کوئیشی
                    </td>
                  </tr>

                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    /// <summary>A code, set large and spaced so it can be read off a phone and typed.</summary>
    public static string Code(string code) => $"""
        <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:0 auto;">
          <tr>
            <td align="center" dir="ltr"
                style="padding:14px 28px;background-color:{Night};border:1px solid {Saffron};border-radius:12px;
                       font-family:'SF Mono',Menlo,Consolas,monospace;font-size:32px;font-weight:700;
                       letter-spacing:8px;color:{Saffron};">
              {Escape(code)}
            </td>
          </tr>
        </table>
        """;

    /// <summary>
    /// A button, with the address written out underneath it: a client that strips the link, or a
    /// person who does not trust one, still has something to work with.
    /// </summary>
    public static string Button(string url, string label) => $"""
        <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:0 auto;">
          <tr>
            <td align="center" style="border-radius:10px;background-color:{Turquoise};">
              <a href="{Escape(url)}"
                 style="display:inline-block;padding:14px 28px;font-family:{Font};font-size:15px;
                        font-weight:700;color:{Night};text-decoration:none;">{Escape(label)}</a>
            </td>
          </tr>
          <tr>
            <td align="center" dir="ltr"
                style="padding-top:14px;font-family:{Font};font-size:12px;color:{Muted};word-break:break-all;">
              {Escape(url)}
            </td>
          </tr>
        </table>
        """;

    /// <summary>
    /// The same message as text. Sent alongside every HTML mail, because a plain-text alternative
    /// is what a text-only client shows and what a spam filter expects to find.
    /// </summary>
    public static string PlainText(string title, string intro, string hero, string note)
    {
        var builder = new StringBuilder();
        builder.AppendLine(title).AppendLine();
        builder.AppendLine(intro).AppendLine();
        builder.AppendLine(hero).AppendLine();
        builder.AppendLine(note).AppendLine();
        builder.Append("Quesshi");
        return builder.ToString();
    }

    /// <summary>
    /// Everything interpolated here came from outside — a display name, a link with a token in it.
    /// A mail is markup like any other, and an unescaped apostrophe in a name breaks the layout
    /// long before anything worse does.
    /// </summary>
    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
