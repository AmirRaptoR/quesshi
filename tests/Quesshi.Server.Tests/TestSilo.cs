using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class TestSilo : ISiloConfigurator
{
    public void Configure(ISiloBuilder silo)
    {
        silo.AddMemoryGrainStorage("hot");
        silo.UseInMemoryReminderService();
        silo.ConfigureServices(services =>
        {
            services.AddSingleton<IClock>(Shared.Clock);
            services.AddSingleton<IQuestionRepository>(Shared.Questions);
            services.AddSingleton<ICategoryRepository>(Shared.Categories);
            services.AddSingleton<IMatchArchive>(Shared.Archive);
            services.AddSingleton<ILeaderboard>(Shared.Leaderboard);
            services.AddSingleton<IPlayerRepository>(Shared.Players);
            services.AddSingleton<ILiveNotifier>(Shared.Notifier);

            // MatchGrain now draws its own question set at Start/auto-start, the way LiveMatchGrain
            // already does — needed for DI to construct the grain at all, even in tests that only ever
            // exercise the legacy pre-drawn Create overload and never actually call BuildAsync.
            services.AddSingleton<QuestionSetBuilder>();
        });
    }
}
