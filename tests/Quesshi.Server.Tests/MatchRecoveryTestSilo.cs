using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

/// <summary>
/// Its own silo, separate from <see cref="TestSilo"/>, for one reason: the "hot" grain storage
/// provider here is <see cref="MatchRecoveryShared.Storage"/>, a fake with a seam for failing one
/// grain's write and for seeding a row directly, in place of Orleans's own in-memory provider — which
/// has no such seam. Registered as a keyed singleton under the same name ("hot") every
/// <c>[PersistentState("...", "hot")]</c> attribute in this codebase already asks for, so MatchGrain
/// (and nothing else this collection's tests touch) is durably backed by it without any change to the
/// grain itself.
/// </summary>
public sealed class MatchRecoveryTestSilo : ISiloConfigurator
{
    public void Configure(ISiloBuilder silo)
    {
        silo.UseInMemoryReminderService();
        silo.ConfigureServices(services =>
        {
            services.AddKeyedSingleton<IGrainStorage>("hot", MatchRecoveryShared.Storage);
            services.AddSingleton<IClock>(MatchRecoveryShared.Clock);
            services.AddSingleton<IQuestionRepository>(MatchRecoveryShared.Questions);
            services.AddSingleton<ICategoryRepository>(MatchRecoveryShared.Categories);
            services.AddSingleton<IMatchArchive>(MatchRecoveryShared.Archive);
            services.AddSingleton<ILeaderboard>(MatchRecoveryShared.Leaderboard);
            services.AddSingleton<IPlayerRepository>(MatchRecoveryShared.Players);
            services.AddSingleton<QuestionSetBuilder>(); // see TestSilo's identical remark
        });
    }
}
