using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Infrastructure.Mongo;

namespace Quesshi.Server.Tests;

/// <summary>
/// <see cref="MatchDoc"/>'s own compatibility formula, exercised directly rather than through Mongo:
/// <see cref="MatchDoc.From"/> backfills <c>OwnerId</c>/<c>Participants</c> from the challenger/opponent
/// pair on every write, and <see cref="MatchDoc.ToDomain"/> reads a row that predates
/// <see cref="ParticipantResult"/> back into two results built from the legacy score fields. Both sides
/// of the null-opponent case are covered — the shape a lobby nobody has joined actually has.
/// </summary>
public class MatchDocBackfillTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void From_backfills_owner_and_participants_for_a_two_player_match()
    {
        var m = new ArchivedMatch("m1", "CODE01", Language.En, "u-challenger", "u-opponent", "u-challenger", false,
            [new ParticipantResult("u-challenger", 80, 1, MatchOutcome.Win), new ParticipantResult("u-opponent", 40, 2, MatchOutcome.Loss)],
            MatchState.Resolved, T0, T0, ["q1", "q2"]);

        var doc = MatchDoc.From(m);

        Assert.Equal("u-challenger", doc.OwnerId);
        Assert.Equal(["u-challenger", "u-opponent"], doc.Participants);
    }

    /// <summary>
    /// The case the spec calls out by name: a lobby nobody has joined has a null opponent, and
    /// backfilling has to produce a one-seat list, never a phantom second participant that would
    /// corrupt standings or contradict the free seat the record is meant to keep open.
    /// </summary>
    [Fact]
    public void From_backfills_a_one_seat_participants_list_when_the_opponent_is_null()
    {
        var m = new ArchivedMatch("m2", "CODE02", Language.En, "u-challenger", null, null, false,
            [new ParticipantResult("u-challenger", 0, 0, MatchOutcome.Loss)],
            MatchState.AwaitingOpponent, T0, null, ["q1", "q2"]);

        var doc = MatchDoc.From(m);

        Assert.Equal("u-challenger", doc.OwnerId);
        Assert.Equal(["u-challenger"], doc.Participants);
    }

    /// <summary>
    /// A document written before <c>Results</c> existed has an empty <see cref="MatchDoc.Results"/> —
    /// BSON leaves an absent array field at its default — so <see cref="MatchDoc.ToDomain"/> has to
    /// fall back to the legacy <c>ChallengerScore</c>/<c>OpponentScore</c> fields, which every such row
    /// really does carry.
    /// </summary>
    [Fact]
    public void ToDomain_reads_a_legacy_row_with_no_Results_back_as_two_participant_results()
    {
#pragma warning disable CS0618 // the two fields this test constructs are exactly what a legacy row has
        var doc = new MatchDoc
        {
            Id = "legacy-1", Code = "OLD01", Lang = (int)Language.En,
            ChallengerId = "u-challenger", OpponentId = "u-opponent",
            WinnerId = "u-challenger", IsDraw = false,
            ChallengerScore = 70, OpponentScore = 30,
            State = (int)MatchState.Resolved, CreatedAt = T0.UtcDateTime, EndedAt = T0.UtcDateTime,
            QuestionIds = ["q1", "q2"]
            // OwnerId, Participants and Results are all left at their defaults -- exactly what a row
            // written before this migration looks like once BSON deserializes it.
        };
#pragma warning restore CS0618

        var m = doc.ToDomain();

        Assert.Equal(
            [new ParticipantResult("u-challenger", 70, 0, MatchOutcome.Loss), new ParticipantResult("u-opponent", 30, 0, MatchOutcome.Loss)],
            m.Results);
    }

    /// <summary>The null-opponent case for the read side too: a legacy lobby nobody joined reads back
    /// as a single result, not a phantom second one for an opponent that was never there.</summary>
    [Fact]
    public void ToDomain_reads_a_legacy_lobby_with_no_opponent_back_as_a_single_result()
    {
#pragma warning disable CS0618
        var doc = new MatchDoc
        {
            Id = "legacy-2", Code = "OLD02", Lang = (int)Language.En,
            ChallengerId = "u-challenger", OpponentId = null,
            IsDraw = false, ChallengerScore = 0, OpponentScore = 0,
            State = (int)MatchState.AwaitingOpponent, CreatedAt = T0.UtcDateTime,
            QuestionIds = ["q1", "q2"]
        };
#pragma warning restore CS0618

        var m = doc.ToDomain();

        Assert.Equal([new ParticipantResult("u-challenger", 0, 0, MatchOutcome.Loss)], m.Results);
    }

    [Fact]
    public void A_document_written_by_From_round_trips_through_ToDomain_via_Results_not_the_legacy_fields()
    {
        var m = new ArchivedMatch("m3", "CODE03", Language.En, "u-challenger", "u-opponent", null, true,
            [new ParticipantResult("u-challenger", 50, 1, MatchOutcome.Draw), new ParticipantResult("u-opponent", 50, 1, MatchOutcome.Draw)],
            MatchState.Resolved, T0, T0, ["q1"]);

        var restored = MatchDoc.From(m).ToDomain();

        Assert.Equal(m.Results, restored.Results);
    }
}
