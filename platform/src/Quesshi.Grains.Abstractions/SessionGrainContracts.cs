using Orleans;

namespace Quesshi.Grains.Abstractions;

/// <summary>The single writer for a session. Its string key is "{tenantId}/{sessionId}".</summary>
public interface ISessionGrain : IGrainWithStringKey
{
    Task<SessionCommandResult> ExecuteAsync(SessionCommand command);
    Task<LobbyCommandResult> ExecuteLobbyAsync(LobbyCommand command);
    Task TimeoutAsync(DateTimeOffset dueAt);
}

/// <summary>JSON crosses the grain boundary as text so contract versioning is explicit.</summary>
[GenerateSerializer]
public sealed record SessionCommand(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] string Type,
    [property: Id(2)] string Phase,
    [property: Id(3)] int Slot,
    [property: Id(4)] int Step,
    [property: Id(5)] int? Revision,
    [property: Id(6)] string? Name,
    [property: Id(7)] string? PayloadJson,
    [property: Id(8)] DateTimeOffset ReceivedAt);

[GenerateSerializer]
public sealed record SessionCommandResult(
    [property: Id(0)] string Code,
    [property: Id(1)] bool Retryable,
    [property: Id(2)] long? Version,
    [property: Id(3)] string? ViewJson)
{
    public static SessionCommandResult Error(string code, bool retryable = false) => new(code, retryable, null, null);
}

/// <summary>Authenticated lobby operation addressed to a tenant-scoped session grain.</summary>
[GenerateSerializer]
public sealed record LobbyCommand(
    [property: Id(0)] string Player,
    [property: Id(1)] string Type,
    [property: Id(2)] string? RulesJson = null,
    [property: Id(3)] string? ContentJson = null,
    [property: Id(4)] int? Capacity = null);

/// <summary>Stable outcome returned by lobby operations before a session is running.</summary>
[GenerateSerializer]
public sealed record LobbyCommandResult(
    [property: Id(0)] string Code,
    [property: Id(1)] long? Version = null)
{
    public static LobbyCommandResult Error(string code) => new(code);
}
