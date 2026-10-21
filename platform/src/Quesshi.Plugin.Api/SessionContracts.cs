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
