using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Features.Stats;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class StatsProfileTests : BunitContext
{
    private const string PlayerId = "21ac7193-52fd-4d4b-9194-5cd547448072";
    private const string CategoryId = "5a0c0c2e-0000-4000-8000-000000000001";
    private const string Summary = $$$"""
        {"playerId":"{{{PlayerId}}}",
         "formats":[
           {"format":"async_duel","played":12,"wins":8,"losses":3,"draws":1,"points":73,"streak":2,"bestStreak":5},
           {"format":"live","played":4,"wins":1,"losses":2,"draws":1,"points":9,"streak":0,"bestStreak":1}],
         "categories":[{"categoryId":"{{{CategoryId}}}","asked":8,"creditSum":6,"names":{"en":"History","fa":"تاریخ"}}],
         "total":481}
        """;

    /// <summary>AC-1.</summary>
    [Fact]
    public async Task The_profile_shows_each_formats_record_and_streaks()
    {
        var cut = await RenderProfileAsync(new StatsServer(Summary), "en");

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-test='stats-format']").Count));
        var duel = cut.Find("[data-test='stats-format'][data-format='async_duel']");
        Assert.Equal("12", duel.QuerySelector("[data-test='stats-played']")!.TextContent);
        Assert.Equal("8", duel.QuerySelector("[data-test='stats-wins']")!.TextContent);
        Assert.Equal("3", duel.QuerySelector("[data-test='stats-losses']")!.TextContent);
        Assert.Equal("1", duel.QuerySelector("[data-test='stats-draws']")!.TextContent);
        Assert.Equal("2", duel.QuerySelector("[data-test='stats-streak']")!.TextContent);
        Assert.Equal("5", duel.QuerySelector("[data-test='stats-best-streak']")!.TextContent);
        var live = cut.Find("[data-test='stats-format'][data-format='live']");
        Assert.Equal("4", live.QuerySelector("[data-test='stats-played']")!.TextContent);
        Assert.Equal("0", live.QuerySelector("[data-test='stats-streak']")!.TextContent);
    }

    /// <summary>AC-2.</summary>
    [Fact]
    public async Task The_profile_has_no_stats_section_when_the_tenant_disables_stats()
    {
        var server = new StatsServer(Summary) { Disabled = true };
        var cut = await RenderProfileAsync(server, "en");

        cut.WaitForState(() => server.Requests > 0);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#profile-title")));
        Assert.Empty(cut.FindAll("[data-test='stats-section']"));
    }

    /// <summary>AC-3.</summary>
    [Theory]
    [InlineData("en", "History")]
    [InlineData("fa", "تاریخ")]
    public async Task Category_accuracy_uses_asked_and_credit_counts_with_the_name_in_the_selected_language(string lang, string expectedName)
    {
        var cut = await RenderProfileAsync(new StatsServer(Summary), lang);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-test='stats-category']")));
        Assert.Equal(expectedName, cut.Find("[data-test='stats-category-name']").TextContent);
        Assert.Equal("75%", cut.Find("[data-test='stats-category-accuracy']").TextContent);
        Assert.Equal("6/8", cut.Find("[data-test='stats-category-counts']").TextContent);
    }

    /// <summary>AC-4, AC-5: the client shows the total the server returns.</summary>
    [Fact]
    public async Task The_profile_total_is_the_value_the_summary_returns()
    {
        var cut = await RenderProfileAsync(new StatsServer(Summary), "en");

        cut.WaitForAssertion(() => Assert.Equal("481", cut.Find("[data-test='stats-total-value']").TextContent));
    }

    private async Task<IRenderedComponent<Profile>> RenderProfileAsync(StatsServer server, string lang)
    {
        var http = new ClientHttp(new HttpClient(server) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        JSInterop.Setup<string?>("quesshi.get", _ => true).SetResult(null);
        var translator = new Translator(http);
        await translator.UseAsync(lang);
        Services.AddSingleton(translator);
        Services.AddSingleton<IClientTranslator>(translator);
        Services.AddSingleton<IClientLanguage>(translator);
        Services.AddSingleton(http);
        var auth = new AuthState(new PlayerApi(http));
        await auth.InitializeAsync();
        Services.AddSingleton(auth);
        var registry = new ClientRegistry(new NoLoader());
        StatsClient.Register(registry);
        Services.AddSingleton<IFormatClientRegistry>(registry);
        return Render<Profile>();
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class StatsServer(string summary) : HttpMessageHandler
    {
        public bool Disabled { get; init; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/me") return Task.FromResult(Json($$"""{"id":"{{PlayerId}}","displayName":"North player","avatarSeed":"avatar-7","lang":"en"}"""));
            if (path.StartsWith("/i18n/", StringComparison.Ordinal)) return Task.FromResult(Json("{}"));
            if (path != $"/api/x/stats/players/{PlayerId}") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            Requests++;
            return Task.FromResult(Disabled ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(summary));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
