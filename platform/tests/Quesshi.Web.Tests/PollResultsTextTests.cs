using System.Net;
using System.Text;
using Quesshi.Formats.Poll;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.Poll;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class PollResultsTextTests
{
    private sealed class FromDisk : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken token)
        {
            var file = Path.Combine(AppContext.BaseDirectory, "i18n",
                Path.GetFileName(request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(File.ReadAllText(file), Encoding.UTF8, "application/json")
            });
        }
    }

    private static async Task<IClientTranslator> EnglishAsync()
    {
        var translator = new Translator(new ClientHttp(new HttpClient(new FromDisk())
        {
            BaseAddress = new Uri("https://quesshi.example/")
        }, new NullReloader()));
        await translator.UseAsync("en");
        return translator;
    }

    [Fact]
    public void A_pair_without_comparable_answers_uses_nothing_to_compare_instead_of_zero_percent()
    {
        var pair = new PollPairStat("p1", "p2", Same: 0, Different: 0,
            AgreementPercent: null);

        Assert.Equal("voting.results.nothingToCompare", PollResultsText.PairAgreementKey(pair));
    }

    [Fact]
    public void A_pair_with_a_percentage_uses_the_percentage_key()
    {
        var pair = new PollPairStat("p1", "p2", Same: 2, Different: 1,
            AgreementPercent: 67);

        Assert.Equal("voting.results.agreementPercent", PollResultsText.PairAgreementKey(pair));
    }

    [Fact]
    public async Task Percentage_and_nothing_to_compare_are_formatted_from_the_shipped_translations()
    {
        var translator = await EnglishAsync();
        var comparable = new PollPairStat("p1", "p2", 2, 1, 67);
        var incomparable = new PollPairStat("p1", "p3", 0, 0, null);

        Assert.Equal("67% agreed", PollResultsText.PairAgreement(comparable, translator));
        Assert.Equal("Nothing to compare", PollResultsText.PairAgreement(incomparable, translator));
    }

    [Fact]
    public async Task No_contest_has_its_own_final_message()
    {
        var translator = await EnglishAsync();

        Assert.Equal("voting.results.noContest", PollResultsText.NoContestKey);
        Assert.Equal("No contest", PollResultsText.NoContest(translator));
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }
}
