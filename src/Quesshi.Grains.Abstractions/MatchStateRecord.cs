namespace Quesshi.Grains.Abstractions;

/// <summary>The whole match, as JSON. See the ponytail note on MatchGrain.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchStateRecord")]
public sealed class MatchStateRecord
{
    [Id(0)] public string Json { get; set; } = "";

    /// <summary>
    /// Settlement progress, as JSON — hand-managed exactly like <see cref="Json"/> rather than as
    /// plain typed properties, so its own absence in a persisted row is unambiguous no matter which
    /// serializer or storage provider ends up carrying this class across an Orleans upgrade. Every
    /// match settled before this field existed has, and will always have, "" here: nothing in the old
    /// code path ever had a reason to write anything else, so "" is the one value old and new code can
    /// never disagree about. <c>MatchGrain.SettleAsync</c> and its resume-on-activation logic treat
    /// "" as "already settled by code that predates this field — never settle again," a non-empty
    /// value carrying <c>Complete: false</c> as "started, resume," and <c>Complete: true</c> as done.
    /// This is the tri-state that makes it safe to reactivate every match ever played, including the
    /// ones the async history listing wakes in bulk the first time anyone opens their duels: a
    /// boolean, defaulting to false the way a missing field naturally would, would read every one of
    /// those as "unsettled" and settle them all a second time.
    /// </summary>
    [Id(1)] public string SettlementJson { get; set; } = "";
}
