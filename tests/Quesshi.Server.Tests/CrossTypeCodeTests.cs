using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// Async and live duels share one code namespace through <see cref="IMatchArchive"/>. Each side must
/// refuse a code that belongs to the other kind rather than half-acting on it.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class CrossTypeCodeTests(ClusterFixture fixture)
{
    private IGrainFactory Grains => fixture.Cluster.GrainFactory;

    private static ArchivedMatch LiveRow(string id, string code, string challengerId) => new(
        id, code, Language.En, challengerId, null, null, false, 0, 0, MatchState.AwaitingOpponent,
        Shared.Clock.Now, null, [], IsLive: true);

    [Fact]
    public async Task Async_join_refuses_a_live_code_and_touches_no_match_grain()
    {
        var id = Guid.NewGuid().ToString("N");
        var code = $"LIVE-{id}";
        Shared.Archive.Items.Add(LiveRow(id, code, "p-challenger"));

        var result = await GameEndpoints.JoinMatchAsync(code, "p-joiner", Grains, Shared.Archive, Shared.Players);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal("not_an_async_code", ErrorOf(result));

        // The row is untouched: nothing about it looks like the async grain ever ran a join against it.
        var view = await Grains.GetGrain<IMatchGrain>(id).GetAsync("p-joiner");
        Assert.Null(view);
    }

    [Fact]
    public async Task Guest_join_refuses_a_live_code_and_creates_no_guest()
    {
        var id = Guid.NewGuid().ToString("N");
        var code = $"LIVE-{id}".ToUpperInvariant();
        Shared.Archive.Items.Add(LiveRow(id, code, "p-challenger"));
        var playersBefore = Shared.Players.Items.Count;

        var result = await AuthEndpoints.GuestJoinAsync(code, new GuestJoinDto("Newcomer"), Shared.Archive,
            Shared.Players, Grains, Issuer, new FakeIdFactory(), Shared.Clock);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal("not_an_async_code", ErrorOf(result));
        Assert.Equal(playersBefore, Shared.Players.Items.Count);
    }

    [Fact]
    public async Task Live_join_refuses_an_async_code_and_touches_no_live_match_grain()
    {
        var asyncId = Guid.NewGuid().ToString("N");
        var asyncCode = $"ASYNC-{asyncId}".ToUpperInvariant();
        Shared.Archive.Items.Add(new ArchivedMatch(asyncId, asyncCode, Language.En, "p-challenger", null, null,
            false, 0, 0, MatchState.AwaitingOpponent, Shared.Clock.Now, null, []));

        var result = await LiveEndpoints.JoinAsync(asyncCode, "p-joiner", Grains, Shared.Archive, Shared.Players,
            LiveShared.Questions, LiveShared.Categories, Shared.Clock);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal("not_a_live_code", ErrorOf(result));
    }

    [Fact]
    public async Task Invite_reports_live_correctly_for_both_kinds()
    {
        var liveId = Guid.NewGuid().ToString("N");
        var liveCode = $"LIVE-{liveId}".ToUpperInvariant();
        Shared.Archive.Items.Add(LiveRow(liveId, liveCode, "p-challenger"));

        var asyncId = Guid.NewGuid().ToString("N");
        var asyncCode = $"ASYNC-{asyncId}".ToUpperInvariant();
        Shared.Archive.Items.Add(new ArchivedMatch(asyncId, asyncCode, Language.En, "p-challenger", null, null,
            false, 0, 0, MatchState.AwaitingOpponent, Shared.Clock.Now, null, []));

        var liveInvite = (InviteDto)ValueOf(await AuthEndpoints.InviteAsync(liveCode, Shared.Archive, Shared.Players));
        var asyncInvite = (InviteDto)ValueOf(await AuthEndpoints.InviteAsync(asyncCode, Shared.Archive, Shared.Players));

        Assert.True(liveInvite.Live);
        Assert.False(asyncInvite.Live);
    }

    [Fact]
    public async Task A_live_row_does_not_appear_in_the_async_match_list_for_either_player()
    {
        var id = Guid.NewGuid().ToString("N");
        var code = $"LIVE-{id}".ToUpperInvariant();
        Shared.Archive.Items.Add(LiveRow(id, code, "p-live-challenger") with { OpponentId = "p-live-opponent" });

        var challengerRows = await GameEndpoints.ListMatchesAsync("p-live-challenger", false, null, Shared.Archive, Shared.Players, Grains);
        var opponentRows = await GameEndpoints.ListMatchesAsync("p-live-opponent", false, null, Shared.Archive, Shared.Players, Grains);

        Assert.DoesNotContain(challengerRows, r => r.Id == id);
        Assert.DoesNotContain(opponentRows, r => r.Id == id);
    }

    private static readonly TokenIssuer Issuer = new(new JwtOptions
    {
        Key = "a-test-signing-key-long-enough-to-use", Issuer = "quesshi", Audience = "quesshi", Days = 1
    });

    /// <summary>
    /// Minimal-API results (<c>Results.BadRequest</c>, <c>Results.NotFound</c>, ...) are internal
    /// generic types; reading them back through reflection avoids pulling the whole ASP.NET Core
    /// hosting surface into these grain-level tests just to assert a status code and an error body.
    /// </summary>
    internal static int StatusOf(object result) => (int)result.GetType().GetProperty("StatusCode")!.GetValue(result)!;

    internal static string ErrorOf(object result)
    {
        var value = ValueOf(result);
        return (string)value.GetType().GetProperty("error")!.GetValue(value)!;
    }

    internal static object ValueOf(object result) => result.GetType().GetProperty("Value")!.GetValue(result)!;
}
