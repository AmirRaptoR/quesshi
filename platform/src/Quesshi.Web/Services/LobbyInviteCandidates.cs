namespace Quesshi.Web.Services;

/// <summary>
/// Who the lobby dialog offers to invite: the recent co-players the hosting page supplies, plus the viewer's
/// friends while the friends feature is enabled. This only chooses what to show; the core invitation endpoint
/// stays the authority on who may actually be invited.
/// </summary>
public sealed class LobbyInviteCandidates(PlayerApi api, AppConfig config)
{
    private const string FriendsFeature = "friends";

    public bool FriendsEnabled =>
        config.Current?.Capabilities.TryGetValue("feature", out var features) == true
        && features.Contains(FriendsFeature, StringComparer.Ordinal);

    public async Task<IReadOnlyList<LobbyInvitee>> MergeAsync(IReadOnlyList<LobbyInvitee> recent, CancellationToken cancellationToken = default)
    {
        if (!FriendsEnabled) return recent;
        var friends = await LoadFriendsAsync(cancellationToken);
        if (friends.Count == 0) return recent;

        var known = recent.Select(invitee => invitee.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. recent, .. friends.Where(friend => known.Add(friend.Id))];
    }

    private async Task<IReadOnlyList<LobbyInvitee>> LoadFriendsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await api.GetAsync<List<FriendEntry>>($"/api/x/{FriendsFeature}", cancellationToken);
            return response.IsSuccess && response.Value is { } entries
                ? entries.Select(entry => new LobbyInvitee(entry.Id.ToString(), entry.DisplayName)).ToArray()
                : [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    private sealed record FriendEntry(Guid Id, string DisplayName);
}
