using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class QuestionReportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static Question Approved() => Question.Create("q1", Language.En, "geography", Difficulty.Easy,
        "A prompt?", ["a", "b", "c", "d"], 0, T0, status: QuestionStatus.Approved);

    [Fact]
    public void A_new_question_has_nothing_against_it()
    {
        var q = Approved();
        Assert.Equal(0, q.ReportCount);
        Assert.True(q.IsPlayable);
    }

    [Fact]
    public void A_player_can_report_a_question()
    {
        var q = Approved();
        Assert.True(q.Report("p1", ReportReason.WrongAnswer, T0));
        Assert.Equal(1, q.ReportCount);
    }

    [Fact]
    public void The_same_player_cannot_report_the_same_question_twice()
    {
        var q = Approved();
        q.Report("p1", ReportReason.WrongAnswer, T0);

        Assert.False(q.Report("p1", ReportReason.Unclear, T0));
        Assert.Equal(1, q.ReportCount);
    }

    [Fact]
    public void One_report_does_not_take_a_question_out_of_play()
    {
        var q = Approved();
        q.Report("p1", ReportReason.WrongAnswer, T0);

        Assert.True(q.IsPlayable);
        Assert.Equal(QuestionStatus.Approved, q.Status);
    }

    [Fact]
    public void Enough_reports_pull_it_out_of_play_by_itself()
    {
        var q = Approved();
        for (var i = 0; i < Question.ReportsBeforeSuppressed; i++)
            q.Report($"p{i}", ReportReason.WrongAnswer, T0);

        Assert.False(q.IsPlayable);
        Assert.Equal(QuestionStatus.Pending, q.Status);
    }

    [Fact]
    public void Dismissing_the_reports_puts_it_back_in_play()
    {
        var q = Approved();
        for (var i = 0; i < Question.ReportsBeforeSuppressed; i++)
            q.Report($"p{i}", ReportReason.WrongAnswer, T0);

        q.DismissReports();
        q.Approve();

        Assert.Equal(0, q.ReportCount);
        Assert.True(q.IsPlayable);
    }

    [Fact]
    public void Reports_survive_an_edit_so_a_half_fixed_question_is_not_forgotten()
    {
        var q = Approved();
        q.Report("p1", ReportReason.Unclear, T0);
        q.Edit(Language.En, "geography", Difficulty.Easy, "A better prompt?", ["a", "b", "c", "d"], 1, null, null);

        Assert.Equal(1, q.ReportCount);
    }

    [Fact]
    public void The_reasons_players_gave_are_kept_for_the_admin_to_read()
    {
        var q = Approved();
        q.Report("p1", ReportReason.WrongAnswer, T0);
        q.Report("p2", ReportReason.Offensive, T0.AddMinutes(5));

        Assert.Equal([ReportReason.WrongAnswer, ReportReason.Offensive], q.Reports.Select(r => r.Reason));
        Assert.Equal("p2", q.Reports[1].PlayerId);
    }

    [Fact]
    public void A_rejected_question_is_never_playable_however_few_reports_it_has()
    {
        var q = Approved();
        q.Reject();
        Assert.False(q.IsPlayable);
    }
}
