using Xunit;

// Each test class owns PostgreSQL resources and may start an Orleans host. Keep the
// workflow's intentional two-process run within the shared PostgreSQL capacity.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
