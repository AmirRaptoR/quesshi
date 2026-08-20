using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
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
            services.AddSingleton<IMatchArchive>(Shared.Archive);
            services.AddSingleton<ILeaderboard>(Shared.Leaderboard);
            services.AddSingleton<IPlayerRepository>(Shared.Players);
        });
    }
}
