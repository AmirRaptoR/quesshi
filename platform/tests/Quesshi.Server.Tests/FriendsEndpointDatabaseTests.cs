using System.Net;
using System.Net.Http.Json;
using Quesshi.Features.Features.Friends;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class FriendsEndpointDatabaseTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;

        _host = new PlatformHttpFixture([new FriendsFeature()]);
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

    private sealed record Friendship(Guid PlayerId, Guid FriendId);

    private sealed record ErrorDocument(string Error);
}
