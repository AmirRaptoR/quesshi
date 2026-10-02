using System.Net;
using System.Text;
using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class VotingResultsTextTests
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

    private static async Task<Translator> EnglishAsync()
    {
        var translator = new Translator(new HttpClient(new FromDisk())
        {
            BaseAddress = new Uri("https://quesshi.example/")
        });
        await translator.UseAsync("en");
        return translator;
    }

    [Fact]
    public void A_pair_without_comparable_answers_uses_nothing_to_compare_instead_of_zero_percent()
    {
        var pair = new VotingPairStatDto("p1", "p2", Same: 0, Different: 0,
            AgreementPercent: null);

        Assert.Equal("voting.results.nothingToCompare", VotingResultsText.PairAgreementKey(pair));
    }

    [Fact]
    public void A_pair_with_a_percentage_uses_the_percentage_key()
    {
        var pair = new VotingPairStatDto("p1", "p2", Same: 2, Different: 1,
            AgreementPercent: 67);

        Assert.Equal("voting.results.agreementPercent", VotingResultsText.PairAgreementKey(pair));
    }

    [Fact]
    public async Task Percentage_and_nothing_to_compare_are_formatted_from_the_shipped_translations()
    {
        var translator = await EnglishAsync();
        var comparable = new VotingPairStatDto("p1", "p2", 2, 1, 67);
        var incomparable = new VotingPairStatDto("p1", "p3", 0, 0, null);

        Assert.Equal("67% agreed", VotingResultsText.PairAgreement(comparable, translator));
        Assert.Equal("Nothing to compare", VotingResultsText.PairAgreement(incomparable, translator));
    }

    [Fact]
    public async Task No_contest_has_its_own_final_message()
    {
        var translator = await EnglishAsync();

        Assert.Equal("voting.results.noContest", VotingResultsText.NoContestKey);
        Assert.Equal("No contest", VotingResultsText.NoContest(translator));
    }
}
