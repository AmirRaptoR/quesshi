using Quesshi.Domain;

namespace Quesshi.Application.UseCases;

public sealed class TopUpOptions
{
    /// <summary>
    /// Run the top-up on a nightly schedule. Off by default: a job that spends money and, with
    /// AutoApprove on, publishes to players unwatched should be switched on deliberately. The
    /// admin panel's button works either way.
    /// </summary>
    public bool Nightly { get; set; }

    /// <summary>
    /// How many <see cref="QuestionKind.Choice"/> questions each (language, category, level) bucket
    /// should hold before we stop generating.
    /// <para>
    /// The name is the old one on purpose. This used to be <i>the</i> target, applied to every
    /// bucket there was; sorting and map questions turned one target into three
    /// (<see cref="TargetFor"/>). Renaming it to <c>ChoiceTargetPerBucket</c> would have been
    /// tidier and would have silently reset every deployment's configured value back to the default
    /// the moment it shipped, because <c>Generation:TargetPerBucket</c> in an appsettings file binds
    /// by name and a name nothing matches simply does not bind.
    /// </para>
    /// </summary>
    public int TargetPerBucket { get; set; } = 25;

    /// <summary>
    /// The same, for <see cref="QuestionKind.Sort"/>. Much smaller than the choice target, and
    /// deliberately so: a sorting question is a heavier thing to write and a heavier thing to
    /// review — nobody can tell at a glance whether four items really are in population order — and
    /// the spec's answer to that is human review of the first runs rather than volume. A duel's mix
    /// falls out of the bank's proportions, so a handful per bucket is already an occasional sort in
    /// a ten-question duel, which is what was asked for.
    /// </summary>
    public int SortTargetPerBucket { get; set; } = 6;

    /// <summary>
    /// The same, for <see cref="QuestionKind.Map"/>. Small for the same reason, plus one of its own:
    /// a map question is only as good as its coordinates, and the pipeline throws away every
    /// candidate whose city does not actually sit in the country it names — so a large target here
    /// mostly buys rejected candidates and paid tokens.
    /// </summary>
    public int MapTargetPerBucket { get; set; } = 6;

    /// <summary>
    /// What one bucket of this kind should hold. Every count and every "is this bucket thin?"
    /// decision goes through here rather than reading a field, because the bug this whole change
    /// exists to fix was one number being applied to buckets it did not describe: with 3067 choice
    /// questions in the bank, a single target made every sort and map bucket look full and nothing
    /// of either kind was ever written.
    /// </summary>
    public int TargetFor(QuestionKind kind) => kind switch
    {
        QuestionKind.Sort => SortTargetPerBucket,
        QuestionKind.Map => MapTargetPerBucket,
        _ => TargetPerBucket
    };

    public int MaxPerRun { get; set; } = 60;
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// Publish generated questions straight away rather than parking them for review. Reviewing
    /// every question by hand does not scale; players reporting the bad ones does. Set false to go
    /// back to reviewing everything up front.
    /// </summary>
    public bool AutoApprove { get; set; } = true;

}
