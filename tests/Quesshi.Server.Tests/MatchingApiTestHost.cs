using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Server.Api;
using Quesshi.Server.Auth;
using Quesshi.Server.Hubs;

namespace Quesshi.Server.Tests;

public sealed class MatchingApiTestHost(TestCluster cluster) : IAsyncDisposable
{
    public const string SigningKey = "a-matching-endpoint-test-signing-key-long";
    public TokenIssuer TokenIssuer { get; } = new(new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 });
    private readonly IHost _host = Build(cluster);

    private static IHost Build(TestCluster cluster) => new HostBuilder()
        .ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthorization();
                services.AddQuesshiAuthentication(
                    new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 },
                    new AdminAuthOptions { Key = "unused-admin-key-long-enough-here", Issuer = "quesshi" },
                    new TokenIssuer(new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 }),
                    new AdminTokenIssuer(new AdminAuthOptions { Key = "unused-admin-key-long-enough-here", Issuer = "quesshi" }));
                services.AddSingleton(cluster.GrainFactory);
                services.AddSingleton<IMatchArchive>(Shared.Archive);
                services.AddSingleton<IPlayerRepository>(Shared.Players);
                services.AddSingleton<IClock>(Shared.Clock);
                services.AddSingleton<IIdFactory>(new FakeIdFactory(Interlocked.Increment(ref _seed) * 10_000));
                services.AddSingleton<IQuestionRepository>(Shared.Questions);
                services.AddSingleton<ICategoryRepository>(Shared.Categories);
                services.AddSignalR();
                services.AddSingleton<ILiveNotifier, SignalRLiveNotifier>();
                services.AddSingleton<IMatchingCategoryRepository>(Shared.MatchingCategories);
                services.AddSingleton<IMatchingQuestionRepository>(Shared.MatchingQuestions);
                services.AddSingleton<IMatchingNotifier>(Shared.MatchingNotifier);
                services.AddSingleton<MatchingQuestionSetBuilder>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapMatching());
                app.UseEndpoints(endpoints => endpoints.MapHub<LiveHub>("/hub/live"));
            });
        }).Start();

    private static int _seed;
    public HttpClient NewClient() => _host.GetTestServer().CreateClient();
    public HubConnection NewHubConnection(string token) => new HubConnectionBuilder()
        .WithUrl("http://localhost/hub/live", options =>
        {
            options.HttpMessageHandlerFactory = _ => _host.GetTestServer().CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();
    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
