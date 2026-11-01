using System.Text.Json;

namespace Quesshi.Plugin.Api;

/// <summary>The durable shape selected when a session is created.</summary>
public enum SessionShape { Lobby, Started, Open }

/// <summary>A participant supplied by the caller for a session that starts immediately.</summary>
public sealed record SessionParticipant(Guid PlayerId, string Role = "player");

/// <summary>Format-owned content selection persisted with a session.</summary>
public sealed record SessionContent(string Source, JsonElement Parameters, int Count);

/// <summary>A transaction-scoped request to create one durable session.</summary>
public sealed record SessionCreateRequest(
    Guid Id,
    string Format,
    SessionContent Content,
    JsonElement Rules,
    int Capacity,
    SessionShape Shape,
    IReadOnlyList<SessionParticipant>? Participants = null);

public enum SessionCreateStatus { Created, Replayed, IdConflict, Invalid, FormatUnavailable, SourceUnavailable, NotEnoughQuestions }

/// <summary>The non-disclosing result of a session creation attempt.</summary>
public sealed record SessionCreateResult(SessionCreateStatus Status, Guid? SessionId = null, string? Code = null, string? Error = null);

/// <summary>Creates durable sessions using the caller's already-open tenant transaction.</summary>
public interface ISessions
{
    Task<SessionCreateResult> CreateAsync(
        ITenantTx transaction,
        Guid creatorId,
        SessionCreateRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Seats a player in a session that is already open, through the session's own grain, which is the only writer.
/// The call is idempotent: repeating it for a player who already holds a seat reports the seat again.
/// </summary>
public interface ISessionSeating
{
    /// <summary>
    /// True when <paramref name="playerId" /> holds a seat afterwards; false when the session refused the join
    /// (ended, expired, full or not open). Any exception means the outcome is not known.
    /// </summary>
    Task<bool> SeatAsync(
        Guid tenantId,
        Guid sessionId,
        Guid playerId,
        Guid pairingToken,
        CancellationToken cancellationToken = default);
}
