using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Server.Api;

namespace Quesshi.Server.Tests;

/// <summary>
/// The dashboard's thin-bucket table, which is the admin's only window into what the bank lacks.
/// <para>
/// The failure being guarded against is the same one that made kind part of the bucket key at all,
/// seen from the reporting side rather than the generating side: with three thousand choice
/// questions in the bank, every bucket looked full, and the sorting and map questions that did not
/// exist were invisible rather than urgent.
/// </para>
/// </summary>
public class ThinBucketTests
{
    private static readonly TopUpOptions Options = new()
    {
        TargetPerBucket = 25, SortTargetPerBucket = 6, MapTargetPerBucket = 6
    };

    private static BucketCount Bucket(QuestionKind kind, int approved, int pending = 0)
        => new(Language.En, "geography", Difficulty.Easy, approved, pending, kind);

    /// <summary>
    /// A full choice bucket does not hide the two empty ones beside it. The counts contain a single
    /// row — the sorting and map buckets are absent precisely because nothing is in them — and the
    /// report has to invent them rather than report what it was handed.
    /// </summary>
    [Fact]
    public void A_full_choice_bucket_still_reports_the_empty_sort_and_map_buckets()
    {
        var report = ThinBuckets.Report([Bucket(QuestionKind.Choice, 40)], Options);

        Assert.DoesNotContain(report, b => b.Kind == "choice");

        var sort = Assert.Single(report, b => b.Kind == "sort");
        Assert.Equal(0, sort.Approved);
        Assert.Equal(0, sort.Pending);
        Assert.Equal("geography", sort.CategoryId);

        Assert.Single(report, b => b.Kind == "map");
    }

    /// <summary>Each kind is counted separately and reported against its own target, so a row can be
    /// read as "4 of 6" instead of "4, alarming" against a threshold describing another kind.</summary>
    [Fact]
    public void Each_kind_is_counted_and_measured_on_its_own()
    {
        var report = ThinBuckets.Report(
            [Bucket(QuestionKind.Choice, 10, 2), Bucket(QuestionKind.Sort, 3), Bucket(QuestionKind.Map, 6)],
            Options);

        var choice = Assert.Single(report, b => b.Kind == "choice");
        Assert.Equal(10, choice.Approved);
        Assert.Equal(2, choice.Pending);
        Assert.Equal(25, choice.Target);

        var sort = Assert.Single(report, b => b.Kind == "sort");
        Assert.Equal(3, sort.Approved);
        Assert.Equal(6, sort.Target);

        // Six of a target of six: full, and therefore not a thin bucket at all.
        Assert.DoesNotContain(report, b => b.Kind == "map");
    }

    /// <summary>Thinnest first, so the twenty-four rows that fit are the twenty-four worth acting on.</summary>
    [Fact]
    public void The_emptiest_buckets_come_first()
    {
        var report = ThinBuckets.Report(
            [Bucket(QuestionKind.Choice, 20), Bucket(QuestionKind.Sort, 5), Bucket(QuestionKind.Map, 1)],
            Options);

        Assert.Equal(["map", "sort", "choice"], report.Select(b => b.Kind));
    }

    [Fact]
    public void A_bank_with_nothing_in_it_reports_nothing()
        => Assert.Empty(ThinBuckets.Report([], Options));

    /// <summary>
    /// Both halves of a bucket's stock count towards its target: a question waiting for review is
    /// one that has already been paid for, and a run that generated it again would be spending twice
    /// for one question.
    /// </summary>
    [Fact]
    public void Pending_questions_count_towards_the_target()
    {
        var report = ThinBuckets.Report([Bucket(QuestionKind.Sort, 2, 4)], Options);

        Assert.DoesNotContain(report, b => b.Kind == "sort");
    }
}
