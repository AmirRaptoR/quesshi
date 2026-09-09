using System.Net;
using System.Text;
using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The wording issue #89 puts in front of a player: the Topics page's one button, and the names and
/// count on the row that opens it. Unlike <see cref="DuelSettingsLine"/> — whose wording has no test
/// because nothing had loaded the real tables before — these run against the shipped i18n files
/// themselves, which the test project already copies beside itself for
/// <see cref="BrowserTranslationTests"/>. That makes them two tests in one: the label is composed
/// right, and the keys it composes from actually exist in every language.
/// </summary>
public class TopicTextTests
{
    /// <summary>Serves <c>wwwroot/i18n/{lang}.json</c> straight off disk, which is all
    /// <see cref="Translator.UseAsync"/> ever asks its HttpClient for.</summary>
    private sealed class FromDisk : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var file = Path.Combine(AppContext.BaseDirectory, "i18n", Path.GetFileName(request.RequestUri!.AbsolutePath));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(File.ReadAllText(file), Encoding.UTF8, "application/json")
            });
        }
    }

    private static async Task<Translator> ReadingAsync(string lang)
    {
        var translator = new Translator(new HttpClient(new FromDisk()) { BaseAddress = new Uri("https://quesshi.example/") });
        await translator.UseAsync(lang);
        return translator;
    }

    private static CategoryDto Category(string id, string en, string fa, string nl)
        => new(id, en, fa, en, "🌍", "#2FBFB0", IsActive: true, SortOrder: 0, NameNl: nl);

    private static readonly List<CategoryDto> Three =
    [
        Category("geo", "Geography", "جغرافیا", "Aardrijkskunde"),
        Category("general", "General", "عمومی", "Algemeen"),
        Category("science", "Science", "علوم", "Wetenschap")
    ];

    // --- the button at the bottom of the topics page -----------------------------------------------

    [Fact]
    public async Task The_done_button_carries_what_was_picked()
        => Assert.Equal("Done · 3 picked", TopicText.Done(3, await ReadingAsync("en")));

    [Fact]
    public async Task One_topic_is_still_counted_rather_than_named()
        => Assert.Equal("Done · 1 picked", TopicText.Done(1, await ReadingAsync("en")));

    /// <summary>Zero topics is not what an empty pick means — the server draws them itself — so the
    /// button says so in words rather than reading "Done · 0 picked".</summary>
    [Fact]
    public async Task Nothing_picked_reads_as_any_topic()
        => Assert.Equal("Done · Any topic", TopicText.Done(0, await ReadingAsync("en")));

    [Fact]
    public async Task The_dutch_button_says_the_same_thing_in_dutch()
    {
        var dutch = await ReadingAsync("nl");

        Assert.Equal("Klaar · 3 gekozen", TopicText.Done(3, dutch));
        Assert.Equal("Klaar · Elk onderwerp", TopicText.Done(0, dutch));
    }

    /// <summary>Persian counts in Persian digits: a Latin 3 sitting in a Persian sentence is exactly
    /// the sort of foreign object <see cref="Translator.Num"/> exists to keep out. The fa string wraps
    /// the count in parentheses rather than en/nl's middle dot (issue #92): a dot followed only by a
    /// plain space then a Persian digit reads, at this font's size, as that digit glued to a Persian
    /// zero — "تمام · ۳" was misread as "تمام ۳۰".</summary>
    [Fact]
    public async Task The_persian_button_counts_in_persian_digits()
        => Assert.Equal("تمام (۳ موضوع)", TopicText.Done(3, await ReadingAsync("fa")));

    // --- the row that opens the page ----------------------------------------------------------------

    [Fact]
    public async Task The_row_names_the_picked_topics_in_the_reader_s_language()
        => Assert.Equal("Geography, General, Science", TopicText.Names(Three, await ReadingAsync("en")));

    [Fact]
    public async Task Persian_names_are_separated_by_a_persian_comma()
        => Assert.Equal("جغرافیا، عمومی، علوم", TopicText.Names(Three, await ReadingAsync("fa")));

    [Fact]
    public async Task Dutch_reads_its_own_names()
        => Assert.Equal("Aardrijkskunde, Algemeen, Wetenschap", TopicText.Names(Three, await ReadingAsync("nl")));

    /// <summary>How much of the bank three topics is, which the count alone cannot say.</summary>
    [Fact]
    public async Task The_row_counts_the_picks_against_what_this_language_offers()
        => Assert.Equal("3 of 13 topics", TopicText.Count(3, 13, await ReadingAsync("en")));

    [Fact]
    public async Task The_persian_count_is_in_persian_digits_too()
        => Assert.Equal("۳ از ۱۳ موضوع", TopicText.Count(3, 13, await ReadingAsync("fa")));
}
