using Quesshi.Infrastructure.Otp;

namespace Quesshi.Server.Tests;

public class EmailTemplateTests
{
    private static string Render(bool rtl = false) =>
        EmailTemplate.Html("Your sign-in code", "Enter this code to sign in.",
            EmailTemplate.Code("482913"), "It expires in 10 minutes.", rtl);

    [Fact]
    public void The_code_survives_into_the_message()
        => Assert.Contains("482913", Render());

    [Fact]
    public void Persian_is_laid_out_right_to_left()
    {
        Assert.Contains("dir=\"rtl\"", Render(rtl: true));
        Assert.Contains("dir=\"ltr\"", Render(rtl: false));
        Assert.DoesNotContain("dir=\"rtl\"", Render(rtl: false));
    }

    /// <summary>
    /// Images and stylesheets are blocked by default in most clients, and webfonts never arrive at
    /// all in Outlook — a mail that reaches for any of them arrives blank or unstyled.
    /// </summary>
    [Fact]
    public void Nothing_is_loaded_from_anywhere()
    {
        var html = Render();

        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("@import", html);
        Assert.DoesNotContain("url(", html);
    }

    /// <summary>Outlook renders with Word: a layout built on divs and flexbox collapses there.</summary>
    [Fact]
    public void The_layout_is_tables_with_inline_styles()
    {
        var html = Render();

        Assert.Contains("<table", html);
        Assert.Contains("role=\"presentation\"", html);
        Assert.Contains("style=\"", html);
        Assert.DoesNotContain("display:flex", html);
        Assert.DoesNotContain("<style", html);
    }

    [Fact]
    public void A_name_that_looks_like_markup_is_escaped_rather_than_rendered()
    {
        var html = EmailTemplate.Html("t", "Hello <script>alert(1)</script> & \"friend\"",
            EmailTemplate.Code("1"), "n", rtl: false);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public void A_reset_link_appears_as_a_button_and_as_readable_text()
    {
        var url = "https://quesshi.example/admin/reset?token=abc123";
        var html = EmailTemplate.Button(url, "Choose a new password");

        // Twice: once as the href, once written out for a client that strips the anchor.
        Assert.Equal(2, html.Split(url).Length - 1);
        Assert.Contains("Choose a new password", html);
    }

    [Fact]
    public void The_plain_text_alternative_carries_the_same_facts()
    {
        var text = EmailTemplate.PlainText("Your sign-in code", "Enter this code.", "482913", "Expires in 10 minutes.");

        Assert.Contains("482913", text);
        Assert.Contains("Your sign-in code", text);
        Assert.Contains("Expires in 10 minutes.", text);
        Assert.DoesNotContain("<", text);
    }
}
