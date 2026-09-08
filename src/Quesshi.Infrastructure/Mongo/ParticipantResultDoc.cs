namespace Quesshi.Infrastructure.Mongo;

/// <summary>Mongo's mirror of <c>Quesshi.Application.Ports.ParticipantResult</c> — a plain mutable
/// class rather than the application-layer record itself, matching how every other embedded document
/// in this collection (see <c>ReportDoc</c>) keeps its Mongo shape independent of the domain/port type
/// it is built from.</summary>
public sealed class ParticipantResultDoc
{
    public string PlayerId { get; set; } = "";
    public int Score { get; set; }
    public int Place { get; set; }
    public int Outcome { get; set; }
}
