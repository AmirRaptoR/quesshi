using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;

namespace Quesshi.Server.Tests;

/// <summary>
/// Its own silo, separate from <see cref="TestSilo"/>, because a live duel needs a controllable
/// clock: <see cref="LiveShared.TimeProvider"/> is installed as the default <see cref="TimeProvider"/>
/// and as the keyed provider grain timers read (<see cref="TimeProviderNames.Grains"/>), while the
/// silo's own background maintenance timers stay pinned to real time
/// (<see cref="TimeProviderNames.ActivationManagement"/>) — advancing the fake clock must not resume
/// those loops inline.
/// </summary>
public sealed class LiveTestSilo : ISiloConfigurator
{
    public void Configure(ISiloBuilder silo)
    {
        silo.AddMemoryGrainStorage("hot");
        silo.UseInMemoryReminderService();
        silo.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(LiveShared.TimeProvider);
            services.AddKeyedSingleton<TimeProvider>(TimeProviderNames.Grains, LiveShared.TimeProvider);
            services.AddKeyedSingleton<TimeProvider>(TimeProviderNames.ActivationManagement, TimeProvider.System);

            services.AddSingleton<IClock>(new TimeProviderClock(LiveShared.TimeProvider));
            services.AddSingleton<IQuestionRepository>(LiveShared.Questions);
            services.AddSingleton<ICategoryRepository>(LiveShared.Categories);
            services.AddSingleton<ILiveNotifier>(LiveShared.Notifier);
            services.AddSingleton<IMatchArchive>(LiveShared.Archive);
            services.AddSingleton<ILobbyNotifier>(LiveShared.LobbyNotifier);
            services.AddSingleton<IIdFactory>(LiveShared.Ids);
            services.AddSingleton<QuestionSetBuilder>();
        });
    }
}
