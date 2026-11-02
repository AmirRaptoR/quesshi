using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.Poll;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class PollResultTests : BunitContext
{
    private readonly Translator translator;
    private readonly LanguageSelection language;

    public PollResultTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new ClientHttp(new HttpClient(new FromDisk()) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        translator = new Translator(http);
        var config = new AppConfig(http, null!);
        language = new LanguageSelection(config, translator, JSInterop.JSRuntime);
        Services.AddSingleton<IClientViewer>(new ClientViewer(language, config, translator, new AuthState(new PlayerApi(http))));
        config.LoadAsync().GetAwaiter().GetResult();
        Services.AddSingleton(translator);
        Services.AddSingleton<IClientTranslator>(translator);
    }

    private const string Finished = """
        {"result":{"slots":[{"slot":0,"counts":[2,0,0],"allAgreed":true},{"slot":1,"counts":[1,1,0],"allAgreed":false},{"slot":2,"counts":[1,0,1],"allAgreed":false}],
         "pairStats":[{"firstPlayer":"amir","secondPlayer":"sara","same":1,"different":1,"agreementPercent":50}],"allAgreedCount":1},
         "slots":[{"slot":0,"answers":[{"player":"amir","value":{"option":"a"}},{"player":"sara","value":{"option":"a"}}],"counts":[2,0,0],"allAgreed":true},
                  {"slot":1,"answers":[{"player":"amir","value":{"option":"a"}},{"player":"sara","value":{"option":"b"}}],"counts":[1,1,0],"allAgreed":false},
                  {"slot":2,"answers":[{"player":"amir","value":{"option":"a"}},{"player":"sara","value":{"notApplicable":true}}],"counts":[1,0,1],"allAgreed":false}],
         "seats":[{"player":"amir","active":true},{"player":"sara","active":true}]}
        """;

    [Fact]
    public async Task The_finished_result_shows_each_questions_counts_and_each_players_choice()
    {
        await language.UseAsync("en");
        var cut = Render(Finished);

        Assert.Equal(["2", "0", "0"], cut.FindAll("[data-poll-slot='0'] [data-poll-count] .numeral").Select(n => n.TextContent));
        Assert.Equal(["1", "1", "0"], cut.FindAll("[data-poll-slot='1'] [data-poll-count] .numeral").Select(n => n.TextContent));
        Assert.Contains("b", cut.Find("[data-poll-slot='1'] [data-poll-answer='sara']").TextContent);
        Assert.Contains("a", cut.Find("[data-poll-slot='1'] [data-poll-answer='amir']").TextContent);
    }

    [Fact]
    public async Task Choice_counts_and_answers_are_labelled_with_the_option_text()
    {
        await language.UseAsync("en");
        var card = "{\"options\":[{\"id\":\"a\",\"text\":\"Tea\"},{\"id\":\"b\",\"text\":\"Coffee\"}]}";
        var body = Finished.Replace("\"slots\":[{\"slot\":0,\"answers\"", "\"slots\":[{\"slot\":0,\"kind\":\"poll_choice\",\"kindVersion\":1,\"card\":" + card + ",\"answers\"")
            .Replace("{\"slot\":1,\"answers\"", "{\"slot\":1,\"kind\":\"poll_choice\",\"kindVersion\":1,\"card\":" + card + ",\"answers\"");
        var cut = Render(body);

        Assert.Equal(["Tea", "Coffee", "Not applicable"], cut.FindAll("[data-poll-slot='1'] [data-poll-count] span:first-child").Select(n => n.TextContent));
        Assert.Equal("Coffee", cut.Find("[data-poll-slot='1'] [data-poll-answer='sara'] span:last-child").TextContent);
    }

    [Fact]
    public async Task Roster_several_and_none_answers_and_count_rows_are_labelled()
    {
        await language.UseAsync("en");
        var card = "{\"options\":[{\"seat\":0,\"name\":\"Amir\"},{\"seat\":1,\"name\":\"Sara\"}]}";
        var body = """
            {"result":{"slots":[{"slot":0,"counts":[0,0,1,1,0],"allAgreed":false}],"pairStats":[{"firstPlayer":"amir","secondPlayer":"sara","same":0,"different":1,"agreementPercent":0}],"allAgreedCount":0},
             "slots":[{"slot":0,"kind":"poll_roster","kindVersion":1,"card":CARD,"answers":[{"player":"amir","value":{"several":true}},{"player":"sara","value":{"none":true}}],"counts":[0,0,1,1,0],"allAgreed":false}],
             "seats":[{"player":"amir","active":true},{"player":"sara","active":true}]}
            """.Replace("CARD", card);
        var cut = Render(body);

        Assert.Equal(["Amir", "Sara", "More than one person", "No one", "Not applicable"], cut.FindAll("[data-poll-count] span:first-child").Select(n => n.TextContent));
        Assert.Equal("More than one person", cut.Find("[data-poll-answer='amir'] span:last-child").TextContent);
        Assert.Equal("No one", cut.Find("[data-poll-answer='sara'] span:last-child").TextContent);
    }

    [Fact]
    public async Task The_finished_result_shows_pairwise_agreement_and_not_applicable_is_labelled_not_counted()
    {
        await language.UseAsync("en");
        var cut = Render(Finished);

        Assert.Contains("50% agreed", cut.Find("[data-poll-pair='amir:sara']").TextContent);
        Assert.Contains("Not applicable", cut.Find("[data-poll-slot='2'] [data-poll-answer='sara']").TextContent);
        Assert.Equal("Nothing to compare.", cut.Find("[data-poll-slot='2'] [data-poll-agreement]").TextContent);
    }

    [Fact]
    public async Task Full_partial_and_no_comparison_wording_follows_the_voting_texts()
    {
        await language.UseAsync("en");
        var cut = Render(Finished);

        Assert.Equal("You all picked the same option.", cut.Find("[data-poll-slot='0'] [data-poll-agreement]").TextContent);
        Assert.Equal("You picked different options.", cut.Find("[data-poll-slot='1'] [data-poll-agreement]").TextContent);
    }

    [Fact]
    public async Task A_pair_with_nothing_to_compare_shows_no_percentage_and_overall_agreement_shows_when_there_are_no_pairs()
    {
        await language.UseAsync("en");
        var noPercent = Finished.Replace("\"agreementPercent\":50", "\"agreementPercent\":null");
        Assert.Equal("Nothing to compare", Render(noPercent).Find("[data-poll-pair] span:last-child").TextContent);

        var noPairs = Finished.Replace("\"pairStats\":[{\"firstPlayer\":\"amir\",\"secondPlayer\":\"sara\",\"same\":1,\"different\":1,\"agreementPercent\":50}]", "\"pairStats\":[]");
        Assert.Equal("Everyone agreed on 1 of 3 questions.", Render(noPairs).Find("[data-poll-overall]").TextContent);
    }

    [Fact]
    public async Task A_poll_without_final_statistics_shows_no_contest_and_no_answers()
    {
        await language.UseAsync("en");
        var cut = Render("""{"result":{"slots":[{"slot":0,"counts":[1,0,0],"allAgreed":false}],"pairStats":null,"allAgreedCount":null},"slots":[],"seats":[]}""");

        Assert.Equal("No contest", cut.Find("[data-poll-no-contest]").TextContent);
        Assert.Empty(cut.FindAll("[data-poll-answer]"));
    }

    [Fact]
    public async Task In_an_rtl_tenant_language_the_result_is_rtl_with_translated_text_and_no_raw_keys()
    {
        await language.UseAsync("fa");
        var fa = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "i18n", "fa.json")))!;
        var cut = Render(Finished);

        Assert.Equal("rtl", cut.Find("[data-poll-result]").GetAttribute("dir"));
        Assert.Equal(fa["voting.results.title"], cut.Find("h2").TextContent);
        Assert.Equal(fa["voting.agreement.allAgreed"], cut.Find("[data-poll-slot='0'] [data-poll-agreement]").TextContent);
        Assert.DoesNotContain("voting.", cut.Markup);
        foreach (var language in new[] { "en", "fa", "nl" })
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "i18n", language + ".json")))!;
            foreach (var key in new[] { "voting.results.title", "voting.agreement.allAgreed", "voting.agreement.disagreed", "voting.agreement.nothingToCompare", "voting.results.agreementPercent", "voting.results.nothingToCompare", "voting.results.noContest", "voting.results.allAgreed", "voting.answer.notApplicable", "result.you" })
                Assert.True(table.ContainsKey(key), $"{language} lacks {key}");
        }
    }

    private IRenderedComponent<PollResult> Render(string body)
    {
        var view = PlatformState.ParseView($$$"""{"id":"{{{LobbyViews.SessionId}}}","code":"ABCD23","format":"poll","status":"finished","phase":"done","slot":-1,"step":0,"version":3,"configVersion":1,"now":"2026-01-01T00:00:00Z","deadline":null,"me":{"id":"amir","seat":0,"role":"player","status":"done","score":0,"place":null,"outcome":null},"roster":{"total":2,"seats":[{"id":"amir","name":"Amir","seat":0,"role":"player","status":"done","score":0,"place":null,"outcome":null,"online":false},{"id":"sara","name":"Sara","seat":1,"role":"player","status":"done","score":0,"place":null,"outcome":null,"online":false}]},"content":{},"rules":{},"body":{{{body}}}}""")!;
        return Render<PollResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(view)));
    }

    private sealed class FromDisk : HttpMessageHandler
    {
        private const string Config = """{"name":"t","languages":[{"code":"en","displayName":"English","direction":"ltr","appName":null},{"code":"fa","displayName":"Persian","direction":"rtl","appName":null},{"code":"nl","displayName":"Dutch","direction":"ltr","appName":null}],"defaultLanguage":"en","capabilities":{},"configVersion":1,"serverBuildId":"x"}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/config")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Config, Encoding.UTF8, "application/json") });
            var file = Path.Combine(AppContext.BaseDirectory, "i18n", Path.GetFileName(request.RequestUri!.AbsolutePath));
            return Task.FromResult(File.Exists(file)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(file), Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }
}
