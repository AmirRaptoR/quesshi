namespace Quesshi.Grains.Abstractions;

/// <summary>
/// Persisted state of <c>LiveMatchmakingGrain</c> (renamed from <c>LiveLobbyGrain</c>, issue #51).
/// The <see cref="Orleans.AliasAttribute"/> below is deliberately left as the type's <em>pre-rename</em>
/// name — see the remarks on why.
/// </summary>
/// <remarks>
/// Orleans identifies a persisted type by its <c>[Alias]</c>, not by its C# name, precisely so a type
/// can be renamed without a blob already sitting in Redis under the old name becoming unreadable.
/// Changing the string here alongside the class name would defeat that: the grain would fail to
/// resolve the type of whatever this grain's single Redis-backed instance (key 0) had already written
/// under the "live-lobby" <c>[PersistentState]</c> key, on the very next deploy. Keeping the string as
/// it was costs nothing and avoids that.
///
/// This does not, on its own, guarantee the rename is free of storage churn: Orleans also derives a
/// grain's own storage identity from its concrete class name by default, and nothing in this codebase
/// overrides that with an explicit grain-type attribute (not for this grain, not for any other). So
/// renaming <c>LiveLobbyGrain</c> to <c>LiveMatchmakingGrain</c> can itself cause the single instance
/// (key 0) to look up a different Redis key than it did before, independent of this file's own alias.
/// That is judged acceptable here and is not specially migrated: the content on the other end of that
/// key is a live queue with a 60-second TTL and, after this same change, invitations that are plain
/// notifications rather than commitments — both are meant to tolerate a blip, and both are being
/// restructured in this change regardless (a pre-rename <c>LiveChallengeView</c> carried settings and
/// no lobby id, which would not mean anything under the new invitation model even if it were found
/// intact). See the issue's own write-up for the full reasoning.
/// </remarks>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveLobbyState")]
public sealed class LiveMatchmakingState
{
    [Id(0)] public List<LiveQueueEntry> Waiting { get; set; } = [];
    [Id(1)] public List<LiveChallengeView> Challenges { get; set; } = [];
}
