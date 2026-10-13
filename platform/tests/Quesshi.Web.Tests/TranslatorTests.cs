using System.Net;
using System.Text;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class TranslatorTests
{
    [Fact]
    public async Task Missing_keys_or_fallbacks_remain_readable_and_known_values_format()
    {
        var translator = CreateTranslator(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"welcome\":\"Welcome, {0}!\",\"known\":\"Known\"}", Encoding.UTF8, "application/json") });

        await translator.UseAsync("en");

        Assert.Equal("missing", translator["missing"]);
        Assert.Equal("Fallback", translator.Or("missing", "Fallback"));
        Assert.Equal("Known", translator.Or("known", "Fallback"));
        Assert.Equal("Welcome, Ada!", translator.Format("welcome", "Ada"));
    }

    [Fact]
    public async Task Numbers_use_persian_digits_only_for_farsi()
    {
        var translator = CreateTranslator(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        await translator.UseAsync("en");
        Assert.Equal("123", translator.Num(123));

        await translator.UseAsync("fa");
        Assert.Equal("۱۲۳", translator.Num(123));
    }

    [Fact]
    public async Task Failed_i18n_fetch_leaves_an_empty_table()
    {
        var translator = CreateTranslator(new HttpRequestException("unavailable"));

        await translator.UseAsync("fa");

        Assert.Equal("missing", translator["missing"]);
        Assert.Equal("Fallback", translator.Or("missing", "Fallback"));
    }

    private static Translator CreateTranslator(params object[] results)
    {
        var client = new HttpClient(new ResultHandler(results)) { BaseAddress = new Uri("https://tenant.example/") };
        return new Translator(new ClientHttp(client, new NoopReloader()));
    }

    private sealed class ResultHandler(params object[] results) : HttpMessageHandler
    {
        private readonly Queue<object> results = new(results);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var result = results.Dequeue();
            return result switch
            {
                Exception exception => Task.FromException<HttpResponseMessage>(exception),
                HttpResponseMessage response => Task.FromResult(response),
                _ => throw new InvalidOperationException("Unexpected test result.")
            };
        }
    }

    private sealed class NoopReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }
}
