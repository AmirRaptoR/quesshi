using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>
/// What the dashboard's "thin buckets" table shows: the buckets below target, each measured against
/// its own kind's target.
///
/// <para>
/// Its own class, and a pure function, because the interesting behaviour is not something an HTTP
/// test can pin down. The bank is shared, the table is capped at two dozen rows and the ordering is
/// by stock, so "does a category full of choice questions still report its empty sorting bucket?"
/// asked through the endpoint would depend on what every other row in the database happened to hold.
/// Asked here it is three counts in and a list out.
/// </para>
/// </summary>
public static class ThinBuckets
{
    /// <summary>How many rows the dashboard shows. Unchanged, but it means fewer categories now
    /// that a category can occupy three rows — a row says something specific enough to act on, and
    /// the generate control fills the buckets whether or not they fit on the page.</summary>
    private const int Rows = 24;

    public static List<BucketDto> Report(IReadOnlyList<BucketCount> buckets, TopUpOptions topUp)
    {
        var counts = buckets.ToDictionary(b => (b.Lang, b.CategoryId, b.Level, b.Kind));

        // The synthesis in the middle is the point of this whole method. BucketCountsAsync can only
        // report buckets that contain something, so a (language, category, level) holding forty
        // choice questions and no sorting questions has no sorting row at all — the row is missing
        // *because* it is empty, which would make the emptiest bucket in the bank the one thing this
        // table could never show. Filling in the absent kinds for every bucket that exists turns
        // "nothing to report" back into "nothing here yet", which is what the admin needed to know.
        return [.. (from key in buckets.Select(b => (b.Lang, b.CategoryId, b.Level)).Distinct()
                    from kind in Enum.GetValues<QuestionKind>()
                    let found = counts.GetValueOrDefault((key.Lang, key.CategoryId, key.Level, kind))
                    let stock = (found?.Approved ?? 0) + (found?.Pending ?? 0)
                    let target = topUp.TargetFor(kind)
                    where stock < target
                    orderby stock, key.CategoryId, key.Lang
                    select new BucketDto(key.Lang.Code(), key.CategoryId, (int)key.Level,
                        found?.Approved ?? 0, found?.Pending ?? 0,
                        kind.ToString().ToLowerInvariant(), target)).Take(Rows)];
    }
}
