using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Server.Api;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// POST /api/report's ownership guard reads the same <see cref="Quesshi.Application.Ports.IMatchArchive.ForPlayerAsync"/>
/// list as <see cref="GameEndpoints.ListMatchesAsync"/>, so a question served only inside a live
/// duel's archive row must be reportable exactly as one served inside an async duel is.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class MatchListLiveReportTests
{
    [Fact]
    public async Task Report_accepts_a_question_seen_only_in_a_live_duels_archive_row_and_still_refuses_a_stranger()
    {
        const string me = "p-livereport-me";
        const string rival = "p-livereport-rival";
        const string liveQuestionId = "livereport-q1";
        const string strangerQuestionId = "livereport-not-mine";

        Shared.Questions.Items.Add(Question.Create(liveQuestionId, Language.En, "geography", Difficulty.Easy,
            "q", ["right", "w1", "w2", "w3"], 0, Shared.Clock.Now, status: QuestionStatus.Approved));
        Shared.Questions.Items.Add(Question.Create(strangerQuestionId, Language.En, "geography", Difficulty.Easy,
            "q", ["right", "w1", "w2", "w3"], 0, Shared.Clock.Now, status: QuestionStatus.Approved));

        await Shared.Archive.SaveAsync(new ArchivedMatch(
            "livereport-1", "livereport-1", Language.En, me, rival, WinnerId: null, IsDraw: false,
            ChallengerScore: 0, OpponentScore: 0, MatchState.InProgress, Shared.Clock.Now, EndedAt: null,
            QuestionIds: [liveQuestionId], IsLive: true));

        var mine = await GameEndpoints.ReportAsync(new ReportQuestionDto(liveQuestionId, "WrongAnswer"), me,
            Shared.Questions, Shared.Archive, Shared.Clock);
        var notMine = await GameEndpoints.ReportAsync(new ReportQuestionDto(strangerQuestionId, "WrongAnswer"), me,
            Shared.Questions, Shared.Archive, Shared.Clock);

        Assert.Equal(200, CrossTypeCodeTests.StatusOf(mine));
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(notMine));
        Assert.Equal("not_your_question", CrossTypeCodeTests.ErrorOf(notMine));
    }
}
