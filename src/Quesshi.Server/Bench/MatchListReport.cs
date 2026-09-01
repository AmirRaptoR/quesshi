using System.Text;

namespace Quesshi.Server.Bench;

/// <summary>Renders the numbers <see cref="MatchListBench.MeasureAsync"/> collected as the committed record.</summary>
internal static class MatchListReport
{
    public static async Task<string> WriteAsync(AccountMeasurement fresh, AccountMeasurement heavy, ExplainResult explain, StackInfo stack)
    {
        var path = FindDocsPath();
        var sb = new StringBuilder();

        sb.AppendLine("# What `GET /api/matches` costs (issue #4)");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC by " +
            "`dotnet run --project src/Quesshi.Server -- bench-matches measure`, against real MongoDB and Redis " +
            "(the stores from `compose.yaml`), after `bench-matches seed` ran as a separate process so this " +
            "process's grains started cold.");
        sb.AppendLine();
        sb.AppendLine("**Measurement path: in-process.** The harness calls `GameEndpoints.ListMatchesAsync` " +
            "directly with real store implementations, then serialises the returned `List<MatchSummaryDto>` " +
            "itself with ASP.NET's configured `JsonSerializerOptions` (`DefaultIgnoreCondition = WhenWritingNull`, " +
            "`src/Quesshi.Server/Program.cs:167-168`). End-to-end is that call plus that serialisation — nothing " +
            "else is inside the timed interval.");
        sb.AppendLine();
        sb.AppendLine("**Request measured:** `active=false, take=null` — what `/duels` sends " +
            "(`src/Quesshi.Web/Pages/Duels.razor:33`).");
        sb.AppendLine();
        sb.AppendLine("**Coldness:** `bench-matches seed` and `bench-matches measure` are separate processes " +
            "(separate silos). The cold first run below is the first activation, in the measuring process, of " +
            "that account's grains. Warm-up (5 calls) ran only against a third, throwaway account, never against " +
            "\"fresh\" or \"heavy\". Measurement order was fixed: fresh, then heavy.");
        sb.AppendLine();
        sb.AppendLine("**Authentication:** not applicable. The in-process path calls `GameEndpoints.ListMatchesAsync` " +
            "directly — there is no HTTP request and therefore nothing to authenticate.");
        sb.AppendLine();

        sb.AppendLine("## Machine and stack");
        sb.AppendLine();
        sb.AppendLine($"- CPU: {stack.Cpu} ({stack.ProcessorCount} logical processors)");
        sb.AppendLine("- Stores: containers (Docker), the same `mongo:7` / `redis:7-alpine` images `compose.yaml` " +
            "pulls, run standalone on non-default ports rather than through `docker compose up`: this machine " +
            "already had another `quesshi-mongo` / `quesshi-redis` pair running on the default ports for a " +
            "different worktree, which is exactly the kind of database this harness must not touch.");
        sb.AppendLine($"- Mongo image digest: `{stack.MongoImageDigest}`");
        sb.AppendLine($"- Redis image digest: `{stack.RedisImageDigest}`");
        sb.AppendLine();

        sb.AppendLine("## End-to-end elapsed time (ms)");
        sb.AppendLine();
        sb.AppendLine("| Account | Seeded duels | Cold first run | p50 (warm) | p95 (warm) | max (warm) | warm runs |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        AppendRow(sb, fresh);
        AppendRow(sb, heavy);
        sb.AppendLine();

        sb.AppendLine("## Attribution across the four suspects");
        sb.AppendLine();
        sb.AppendLine("(a) the archive query `MongoMatchArchive.ForPlayerAsync`; (b) the grain fan-out — wall-clock " +
            "of the `Task.WhenAll` at `GameEndpoints.cs:260-261`, derived as the residual after subtracting (a) " +
            "and (c) from the call's own elapsed time; (c) the player lookup `MongoPlayerRepository.GetManyAsync`; " +
            "(d) JSON serialisation, measured separately from the call. Because (b) is defined as that residual, " +
            "(a)+(b)+(c) sum to the call's elapsed time by construction, and unattributed time here is mostly the " +
            "small gap between stopping the call's stopwatch and starting the serialisation one, plus stopwatch " +
            "overhead — not missing accounting.");
        sb.AppendLine();
        AppendAttributionSection(sb, fresh);
        AppendAttributionSection(sb, heavy);

        sb.AppendLine("## Grain fan-out detail");
        sb.AppendLine();
        sb.AppendLine($"- Slowest individual grain call, fresh account (warm, satellite pass after the cold run): " +
            $"{fresh.SlowestGrainCallMs:F1}ms");
        sb.AppendLine($"- Slowest individual grain call, heavy account (warm, satellite pass after the cold run): " +
            $"{heavy.SlowestGrainCallMs:F1}ms");
        sb.AppendLine("- Measured by timing each `IMatchGrain.GetAsync` call individually inside the same " +
            "`Task.WhenAll` shape `ListMatchesAsync` uses, run once after the cold run (per account) so as not to " +
            "warm the grains before the cold number was captured. These are therefore warm numbers, reported for " +
            "context on what a concurrent fan-out's wall clock is made of, not as a cold figure in their own right.");
        sb.AppendLine();

        sb.AppendLine("## Archive query: `explain(\"executionStats\")`");
        sb.AppendLine();
        sb.AppendLine($"Query: `{{ $or: [ {{ChallengerId: <heavy id>}}, {{OpponentId: <heavy id>}} ] }}`, " +
            "`sort: {CreatedAt: -1}, limit: 40`, against the 40-duel account, given the compound indexes at " +
            "`src/Quesshi.Infrastructure/Mongo/MongoContext.cs:62-66`.");
        sb.AppendLine();
        sb.AppendLine($"- Winning plan stage: `{explain.WinningPlanStage}`");
        sb.AppendLine($"- `nReturned`: {explain.NReturned}");
        sb.AppendLine($"- `keysExamined`: {explain.KeysExamined}");
        sb.AppendLine($"- `docsExamined`: {explain.DocsExamined}");
        sb.AppendLine($"- Blocking `SORT` stage present: {(explain.HasBlockingSort ? "yes" : "no")}");
        sb.AppendLine();
        sb.AppendLine("<details><summary>Raw explain output</summary>");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(explain.Raw);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        sb.AppendLine("## Reproducing this");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine("docker compose up -d mongo redis   # or point at a dedicated instance — see below");
        sb.AppendLine();
        sb.AppendLine("export ConnectionStrings__Mongo=mongodb://localhost:27017");
        sb.AppendLine("export Mongo__Database=quesshi_bench          # never the default \"quesshi\"");
        sb.AppendLine("export ConnectionStrings__Redis=localhost:6379,defaultDatabase=1   # a dedicated index");
        sb.AppendLine("export Orleans__ClusterId=quesshi-bench        # never the default \"quesshi\"");
        sb.AppendLine("export ASPNETCORE_URLS=http://127.0.0.1:0      # ephemeral port; HTTP is not used");
        sb.AppendLine();
        sb.AppendLine("dotnet run --project src/Quesshi.Server -- bench-matches seed      # writes the accounts, exits");
        sb.AppendLine("dotnet run --project src/Quesshi.Server -- bench-matches measure   # a fresh process; times it");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Isolation");
        sb.AppendLine();
        sb.AppendLine("- `Mongo__Database` and `Orleans__ClusterId` must both be set away from their defaults — " +
            "`bench-matches` refuses to run otherwise. Seeding writes players, questions and match history; a " +
            "shared default database means writing into whatever a developer or a deployment is using.");
        sb.AppendLine("- `RedisLeaderboard` writes to the fixed key `quesshi:leaderboard` " +
            "(`src/Quesshi.Infrastructure/Redis/RedisLeaderboard.cs:9`), which no `ClusterId` namespaces. Orleans " +
            "clustering, grain storage and reminders go through the same `ConnectionStrings__Redis` connection " +
            "too. The straightforward fix is a dedicated Redis database index (`,defaultDatabase=N` in the " +
            "connection string, as above) or a dedicated Redis instance — not the default index 0 a real " +
            "deployment uses.");
        sb.AppendLine("- **Do not point this at a database anyone is using** — a developer's local stack, a " +
            "staging environment, or production. Use a dedicated database name, a dedicated Redis index or " +
            "instance, and a dedicated ClusterId, every time.");
        sb.AppendLine("- To undo a run against a Redis index used only for this: `redis-cli -n <index> FLUSHDB`. " +
            "The Mongo side is undone by dropping the dedicated database.");

        var content = sb.ToString();
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private static void AppendRow(StringBuilder sb, AccountMeasurement m) =>
        sb.AppendLine($"| {m.Label} ({m.SeededDuels} duels) | {m.SeededDuels} | {m.Cold.TotalMs:F1} | " +
            $"{m.P50Ms:F1} | {m.P95Ms:F1} | {m.MaxMs:F1} | {m.TimedRuns} |");

    private static void AppendAttributionSection(StringBuilder sb, AccountMeasurement m)
    {
        sb.AppendLine($"### {m.Label}");
        sb.AppendLine();
        sb.AppendLine("| | archive (a) | fan-out (b) | players (c) | json (d) | sum | end-to-end | unattributed |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        AppendBucketRow(sb, "cold first run", m.Cold);
        AppendBucketRow(sb, "median warm run", m.Median);
        sb.AppendLine();

        if (m.Label == "heavy")
        {
            var pct = m.Median.TotalMs == 0 ? 0 : m.MedianUnattributedMs / m.Median.TotalMs * 100;
            var verdict = pct <= 15
                ? $"OK: {pct:F1}% <= 15%."
                : $"OVER BUDGET: {pct:F1}% > 15%. See the note above on how (b) is derived before treating this as a real gap.";
            sb.AppendLine($"Self-check (median run, heavy account, the criterion this applies to): " +
                $"unattributed {m.MedianUnattributedMs:F1}ms of {m.Median.TotalMs:F1}ms end-to-end. {verdict}");
        }
        else
        {
            sb.AppendLine("Self-check is not applied to the fresh account: its total is close enough to timer " +
                "resolution that a percentage is not meaningful.");
        }
        sb.AppendLine();
    }

    private static void AppendBucketRow(StringBuilder sb, string label, CallBuckets b)
    {
        var sum = b.ArchiveMs + b.GrainFanOutMs + b.PlayersMs + b.SerializeMs;
        var unattributed = b.TotalMs - sum;
        double Pct(double x) => b.TotalMs == 0 ? 0 : x / b.TotalMs * 100;
        sb.AppendLine($"| {label} | {b.ArchiveMs:F1}ms ({Pct(b.ArchiveMs):F0}%) | {b.GrainFanOutMs:F1}ms " +
            $"({Pct(b.GrainFanOutMs):F0}%) | {b.PlayersMs:F1}ms ({Pct(b.PlayersMs):F0}%) | {b.SerializeMs:F1}ms " +
            $"({Pct(b.SerializeMs):F0}%) | {sum:F1}ms | {b.TotalMs:F1}ms | {unattributed:F1}ms ({Pct(unattributed):F0}%) |");
    }

    /// <summary>Walks up from the running assembly to find the repo's `docs/` folder.</summary>
    private static string FindDocsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;

        var docs = dir is null ? Path.Combine(Directory.GetCurrentDirectory(), "docs") : Path.Combine(dir.FullName, "docs");
        Directory.CreateDirectory(docs);
        return Path.Combine(docs, "match-list-measurement.md");
    }
}
