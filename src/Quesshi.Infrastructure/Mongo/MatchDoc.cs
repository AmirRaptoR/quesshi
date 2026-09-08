using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

/// <remarks>Extra elements are ignored so a removed field cannot break start-up.</remarks>
[BsonIgnoreExtraElements]
public sealed class MatchDoc
{
    [BsonId] public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public int Lang { get; set; }
    public string ChallengerId { get; set; } = "";
    public string? OpponentId { get; set; }

    /// <summary>
    /// Backfilled from <see cref="ChallengerId"/>/<see cref="OpponentId"/> by every call to
    /// <see cref="From"/> — real, persisted fields rather than something computed at read time,
    /// because the multikey index <c>MongoContext.EnsureIndexesAsync</c> builds on
    /// <see cref="Participants"/> needs an actual array to index. Unlike the Redis-backed grain
    /// snapshots this migration also tolerates, a Mongo collection can be bulk-updated safely, so
    /// <c>MongoContext</c> runs a one-time backfill over every row already missing these two instead
    /// of leaving them permanently absent on documents this code never happens to rewrite.
    /// </summary>
    public string OwnerId { get; set; } = "";
    public List<string> Participants { get; set; } = [];

    public string? WinnerId { get; set; }
    public bool IsDraw { get; set; }

    /// <summary>
    /// Legacy score fields: real data on every row written before <see cref="Results"/> existed, kept
    /// mapped (not dropped) purely so <see cref="ToDomain"/> can still build two
    /// <see cref="ParticipantResult"/>s out of a document this code has not rewritten. <see cref="From"/>
    /// no longer sets them — <see cref="Results"/> is the one source of truth for a participant's score
    /// from this point on, and a document this code writes carries these two at their default.
    /// </summary>
    public int ChallengerScore { get; set; }
    public int OpponentScore { get; set; }

    public List<ParticipantResultDoc> Results { get; set; } = [];

    public int State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public List<string> QuestionIds { get; set; } = [];

    /// <summary>Discriminates a live duel from an async one in the shared collection. Absent on an
    /// old row, which BSON leaves as the default false — an existing row still reads as async.</summary>
    public bool IsLive { get; set; }

    public static MatchDoc From(ArchivedMatch m) => new()
    {
        Id = m.Id, Code = m.Code, Lang = (int)m.Lang, ChallengerId = m.ChallengerId, OpponentId = m.OpponentId,
        OwnerId = m.ChallengerId,
        // Every real seat, from m.Results — not the two-scalar ChallengerId/OpponentId pair this used
        // to build the array from. Participants is the field the multikey index and ForPlayerAsync's
        // AnyEq filter both query, so building it from only the first two seats meant a capacity>2
        // duel's third-and-later players could never find their own match in "/api/matches" at all:
        // the archive row existed, carried their real ParticipantResult, and was simply unreachable by
        // their own id. m.Results already lists every participant that actually exists (see its own
        // remarks), so this needs nothing beyond projecting PlayerId out of it.
        Participants = [.. m.Results.Select(r => r.PlayerId)],
        WinnerId = m.WinnerId, IsDraw = m.IsDraw,
        Results = [.. m.Results.Select(r => new ParticipantResultDoc
        {
            PlayerId = r.PlayerId, Score = r.Score, Place = r.Place, Outcome = (int)r.Outcome
        })],
        State = (int)m.State, CreatedAt = m.CreatedAt.UtcDateTime, EndedAt = m.EndedAt?.UtcDateTime,
        QuestionIds = m.QuestionIds, IsLive = m.IsLive
    };

    public ArchivedMatch ToDomain()
    {
        // Results.Count == 0 is the tell for a row written before this field existed — every document
        // this code has ever written carries at least one entry (ChallengerId's own), so an empty list
        // here can only mean the row predates Results and its score has to be read off the legacy pair
        // instead, exactly as MatchSnapshot's own legacy branch reads Lang/ChallengerId/OpponentId.
        var results = Results.Count > 0
            ? [.. Results.Select(r => new ParticipantResult(r.PlayerId, r.Score, r.Place, (MatchOutcome)r.Outcome))]
            : LegacyResults(ChallengerId, OpponentId, ChallengerScore, OpponentScore);

        return new ArchivedMatch(Id, Code, (Language)Lang, ChallengerId, OpponentId, WinnerId, IsDraw,
            results, (MatchState)State, new DateTimeOffset(CreatedAt, TimeSpan.Zero),
            EndedAt is null ? null : new DateTimeOffset(EndedAt.Value, TimeSpan.Zero), QuestionIds, IsLive);
    }

    /// <summary>Only for participants that actually exist: a null <paramref name="opponentId"/> means
    /// a lobby nobody had joined when this row was last written, and gets one result, not two.</summary>
    private static List<ParticipantResult> LegacyResults(string challengerId, string? opponentId, int challengerScore, int opponentScore) =>
        opponentId is null
            ? [new ParticipantResult(challengerId, challengerScore, 0, MatchOutcome.Loss)]
            : [new ParticipantResult(challengerId, challengerScore, 0, MatchOutcome.Loss),
               new ParticipantResult(opponentId, opponentScore, 0, MatchOutcome.Loss)];
}
