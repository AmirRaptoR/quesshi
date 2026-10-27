using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Features.Features.Friends;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class FriendsEndpointDatabaseTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private readonly FixedTimeProvider _time = new(Now);
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;

        _host = new PlatformHttpFixture([new FriendsFeature(), new FriendsQueryConsumerFeature()])
        {
            ConfigureServices = services => services.AddSingleton<TimeProvider>(_time),
        };
        await _host.StartAsync();
        await _host.SetCapabilityAsync("feature", "friends", true);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Player_add_creates_the_two_reciprocal_friendship_rows()
    {
        var (_, friendId) = await Host.CreateSecondPlayerClientAsync();

        using var response = await Host.Player.PostAsync($"/api/x/friends/{friendId}", content: null);
        var friendships = await FriendshipsAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equivalent(
            new[] { new Friendship(Host.PlayerId, friendId), new Friendship(friendId, Host.PlayerId) },
            friendships);
    }

    [SkippableFact]
    public async Task Repeated_player_add_is_idempotent()
    {
        var (_, friendId) = await Host.CreateSecondPlayerClientAsync();
        using var first = await Host.Player.PostAsync($"/api/x/friends/{friendId}", content: null);
        using var second = await Host.Player.PostAsync($"/api/x/friends/{friendId}", content: null);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await FriendshipsAsync()).Count);
    }

    [SkippableFact]
    public async Task Player_remove_deletes_both_reciprocal_friendship_rows()
    {
        var (_, friendId) = await Host.CreateSecondPlayerClientAsync();
        using var added = await Host.Player.PostAsync($"/api/x/friends/{friendId}", content: null);
        added.EnsureSuccessStatusCode();

        using var response = await Host.Player.DeleteAsync($"/api/x/friends/{friendId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await FriendshipsAsync());
    }

    [SkippableFact]
    public async Task Opposite_direction_adds_complete_without_a_deadlock_and_leave_two_rows()
    {
        var (friend, friendId) = await Host.CreateSecondPlayerClientAsync();

        var responses = await Task.WhenAll(
            Host.Player.PostAsync($"/api/x/friends/{friendId}", content: null),
            friend.PostAsync($"/api/x/friends/{Host.PlayerId}", content: null));
        using var first = responses[0];
        using var second = responses[1];

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equivalent(
            new[] { new Friendship(Host.PlayerId, friendId), new Friendship(friendId, Host.PlayerId) },
            await FriendshipsAsync());
    }

    [SkippableFact]
    public async Task Guest_cannot_add_a_friend()
    {
        var (_, friendId) = await Host.CreateSecondPlayerClientAsync();

        using var response = await Host.Guest.PostAsync($"/api/x/friends/{friendId}", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Adding_a_guest_as_a_friend_is_refused()
    {
        var (_, guestId) = await Host.CreateAnotherGuestClientAsync();

        using var response = await Host.Player.PostAsync($"/api/x/friends/{guestId}", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Adding_oneself_as_a_friend_is_refused()
    {
        using var response = await Host.Player.PostAsync($"/api/x/friends/{Host.PlayerId}", content: null);
        var error = await response.Content.ReadFromJsonAsync<ErrorDocument>();

        Assert.Equal((HttpStatusCode.BadRequest, "self"), (response.StatusCode, error?.Error));
    }

    [SkippableFact]
    public async Task Player_list_includes_a_friends_display_name_and_avatar()
    {
        var friendId = await CreateFriendAsync();
        var friends = await GetFriendsAsync(Host.Player);

        Assert.Equivalent(new[] { new FriendListEntry(friendId, "Second fixture player", "second", false) }, friends);
    }

    [SkippableFact]
    public async Task Player_list_marks_a_friend_seen_within_sixty_seconds_as_online()
    {
        var friendId = await CreateFriendAsync();
        await SetLastSeenAsync(friendId, Now.AddSeconds(-59));
        var friend = Assert.Single(await GetFriendsAsync(Host.Player));

        Assert.True(friend.IsOnline);
    }

    [SkippableFact]
    public async Task Player_list_marks_a_friend_seen_more_than_sixty_seconds_ago_as_offline()
    {
        var friendId = await CreateFriendAsync();
        await SetLastSeenAsync(friendId, Now.AddSeconds(-61));
        var friend = Assert.Single(await GetFriendsAsync(Host.Player));

        Assert.False(friend.IsOnline);
    }

    [SkippableFact]
    public async Task Guest_cannot_list_friends()
    {
        using var response = await Host.Guest.GetAsync("/api/x/friends");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Named_friend_ids_query_is_available_when_friends_is_enabled()
    {
        var result = await InvokeFriendIdsAsync(Host.PlayerId);

        Assert.True(result.Available);
    }

    [SkippableFact]
    public async Task Named_friend_ids_query_matches_the_friends_list()
    {
        await CreateFriendAsync();
        var friends = await GetFriendsAsync(Host.Player);
        var query = await InvokeFriendIdsAsync(Host.PlayerId);

        Assert.Equal(friends.Select(static friend => friend.Id), query.Value);
    }

    [SkippableFact]
    public async Task Player_friends_route_is_unavailable_when_friends_is_disabled()
    {
        await Host.SetCapabilityAsync("feature", "friends", false);
        using var response = await Host.Player.GetAsync("/api/x/friends");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Named_friend_ids_query_is_unavailable_when_friends_is_disabled()
    {
        await Host.SetCapabilityAsync("feature", "friends", false);
        var result = await InvokeFriendIdsAsync(Host.PlayerId);

        Assert.False(result.Available);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("Platform host did not start.");
        }
    }

    private async Task<IReadOnlyList<Friendship>> FriendshipsAsync() => await Host.QueryOwnerAsync<Friendship>("""
        select player_id as PlayerId, friend_id as FriendId
        from friendships
        where tenant_id = @tenantId
        order by player_id, friend_id
        """, new { tenantId = Host.TenantId });

    private async Task<Guid> CreateFriendAsync()
    {
        var (_, friendId) = await Host.CreateSecondPlayerClientAsync();
        using var response = await Host.Player.PostAsync($"/api/x/friends/{friendId}", content: null);
        response.EnsureSuccessStatusCode();
        return friendId;
    }

    private async Task<IReadOnlyList<FriendListEntry>> GetFriendsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/x/friends");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<FriendListEntry>>() ?? [];
    }

    private async Task<NamedQueryResult<IReadOnlyList<Guid>>> InvokeFriendIdsAsync(Guid playerId)
    {
        var database = Host.Services.GetRequiredService<ITenantDb>();
        var queries = Host.Services.GetRequiredService<INamedQueries>();
        return await database.InTenantAsync(Host.TenantId, transaction =>
            queries.InvokeAsync<IReadOnlyList<Guid>>("friends_read_test", "friends.ids", transaction, playerId, CancellationToken.None), readOnly: true);
    }

    private Task SetLastSeenAsync(Guid playerId, DateTimeOffset lastSeenAt) => Host.ExecuteOwnerAsync("""
        update players
        set last_seen_at = @lastSeenAt
        where tenant_id = @tenantId and id = @playerId
        """, new { tenantId = Host.TenantId, playerId, lastSeenAt });

    private sealed record Friendship(Guid PlayerId, Guid FriendId);

    private sealed record ErrorDocument(string Error);

    private sealed class FriendsQueryConsumerFeature : IFeature
    {
        public string Id => "friends_read_test";

        public FeatureSettings Settings => FeatureSettings.Create(new ConsumerSettings(), static _ => []);

        public void Configure(IFeatureBuilder feature) => feature.Uses("friends.ids");

        private sealed record ConsumerSettings;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
