namespace Quesshi.Shared;

public sealed record AdminDashboardDto(long Players, long Questions, long Approved, long Pending, long Matches,
    List<BucketDto> ThinBuckets, List<GenerationRunDto> Runs, bool GeneratorConfigured,
    AiSpendDto SpendAllTime, AiSpendDto SpendMonth, string Model);
