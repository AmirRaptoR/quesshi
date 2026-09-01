using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Infrastructure.Mongo;
using Quesshi.Server.Api;
using Quesshi.Shared;
using StackExchange.Redis;

namespace Quesshi.Server.Bench;

/// <summary>
/// The harness for issue #4: what <c>GET /api/matches</c> actually costs against real Mongo and
/// Redis, on an account with a long history versus a fresh one. Two commands, run as two separate
/// processes so the second one's grains are genuinely cold:
///
///   dotnet run --project src/Quesshi.Server -- bench-matches seed       (writes the accounts, then exits)
///   dotnet run --project src/Quesshi.Server -- bench-matches measure    (a fresh process; times it)
///
/// See docs/match-list-measurement.md for the numbers this produced and how to reproduce them.
/// </summary>
internal static class MatchListBench
{
    // A throwaway third account: warm-up runs against this one, never against the two being measured,
    // so JIT and connection-pool costs land here instead of contaminating the numbers that matter.
    private const int WarmupDuels = 3;

    // "Fresh" per the issue: 0-2 duels.
    private const int FreshDuels = 2;

    // "History" per the issue: at least 40 finished duels.
    private const int HeavyDuels = 40;

    private const string WarmupId = "bench-warmup-me";
    private const string FreshId = "bench-fresh-me";
    private const string HeavyId = "bench-heavy-me";

    private const int TimedRuns = 20;

    internal static string MatchId(string prefix, int i) => $"bench-{prefix}-m{i}";
    private static string RivalId(string prefix) => $"bench-{prefix}-rival";
    private static string MeId(string prefix) => $"bench-{prefix}-me";

    // ------------------------------------------------------------------------------------------
    // seed
    // ------------------------------------------------------------------------------------------

    public static async Task<int> SeedAsync(IServiceProvider services)
    {
        var mongo = services.GetRequiredService<MongoOptions>();
        var config = services.GetRequiredService<IConfiguration>();
        if (!GuardIsolation(mongo, config, out var isolationError)) return Fail(isolationError);

        Console.WriteLine($"[bench] seeding into Mongo database \"{mongo.Database}\", Orleans ClusterId " +
            $"\"{config["Orleans:ClusterId"]}\". Redis isolation (a dedicated database index or instance) is not " +
            "checked here — see docs/match-list-measurement.md \"Isolation\" before pointing this at a shared Redis.");

        var grains = services.GetRequiredService<IGrainFactory>();
        var players = services.GetRequiredService<IPlayerRepository>();
        var questions = services.GetRequiredService<IQuestionRepository>();
        var clock = services.GetRequiredService<IClock>();

        await SeedAccountAsync(grains, players, questions, clock, "warmup", WarmupDuels);
        await SeedAccountAsync(grains, players, questions, clock, "fresh", FreshDuels);
        await SeedAccountAsync(grains, players, questions, clock, "heavy", HeavyDuels);

        Console.WriteLine($"[bench] seeded: warmup={WarmupDuels}, fresh={FreshDuels}, heavy={HeavyDuels} finished " +
            $"duels. Players: {WarmupId}, {FreshId}, {HeavyId}. Idempotent — safe to run again.");
        Console.WriteLine("[bench] now run \"bench-matches measure\" as a SEPARATE process (a fresh silo), so the " +
            "grains for \"fresh\" and \"heavy\" are cold at their first timed activation.");
        return 0;
    }

    private static async Task SeedAccountAsync(IGrainFactory grains, IPlayerRepository players, IQuestionRepository questions,
        IClock clock, string prefix, int count)
    {
        var (meId, rivalId) = (MeId(prefix), RivalId(prefix));
        await players.UpsertAsync(Player.Register(meId, $"{meId}@bench.invalid", $"Bench {prefix}", Language.En, clock.Now));
        await players.UpsertAsync(Player.Register(rivalId, $"{rivalId}@bench.invalid", $"Bench {prefix} rival", Language.En, clock.Now));

        for (var i = 0; i < count; i++)
        {
            var matchId = MatchId(prefix, i);
            var questionIds = await SeedQuestionsAsync(questions, matchId);

            var grain = grains.GetGrain<IMatchGrain>(matchId);
            await grain.CreateAsync((int)Language.En, meId, questionIds, $"{prefix}{i:D4}".ToUpperInvariant());
            await grain.JoinAsync(rivalId);

            // Both sides play to completion, so the row lands in Resolved state — the case that
            // dominates a long history and the one the issue is about.
            await PlayToFinishAsync(grain, meId, correctCount: 7);
            await PlayToFinishAsync(grain, rivalId, correctCount: 4);
        }
    }

    private static async Task<List<string>> SeedQuestionsAsync(IQuestionRepository questions, string matchId)
    {
        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var id = $"{matchId}-q{slot}";
            await questions.UpsertAsync(Question.Create(id, Language.En, "geography", MatchRules.LevelForSlot(slot),
                $"bench question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, DateTimeOffset.UtcNow,
                status: QuestionStatus.Approved));
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// Idempotent: if this match was already finished by an earlier seed run, <c>ServeNextAsync</c>
    /// returns null immediately and this is a no-op — which is what lets "seed" be run twice.
    /// </summary>
    private static async Task PlayToFinishAsync(IMatchGrain grain, string playerId, int correctCount)
    {
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var served = await grain.ServeNextAsync(playerId);
            if (served is null) return;
            await grain.AnswerAsync(playerId, served.Slot, slot < correctCount ? 0 : 1);
        }
    }

    // ------------------------------------------------------------------------------------------
    // measure
    // ------------------------------------------------------------------------------------------

    public static async Task<int> MeasureAsync(IServiceProvider services)
    {
        var mongo = services.GetRequiredService<MongoOptions>();
        var config = services.GetRequiredService<IConfiguration>();
        if (!GuardIsolation(mongo, config, out var isolationError)) return Fail(isolationError);

        var archive = services.GetRequiredService<IMatchArchive>();
        var players = services.GetRequiredService<IPlayerRepository>();
        var grains = services.GetRequiredService<IGrainFactory>();
        var mongoContext = services.GetRequiredService<MongoContext>();
        var redis = services.GetRequiredService<IConnectionMultiplexer>();
        var jsonOptions = services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        Console.WriteLine("[bench] measurement path: IN-PROCESS — calls GameEndpoints.ListMatchesAsync directly " +
            "against real stores and a real grain factory, then serialises the result with ASP.NET's own " +
            "JsonSerializerOptions (DefaultIgnoreCondition = WhenWritingNull). End-to-end = that call plus that " +
            "serialisation; nothing else counts.");
        Console.WriteLine($"[bench] request measured: active=false, take=null — what /duels actually sends. " +
            $"Mongo database \"{mongo.Database}\", Orleans ClusterId \"{config["Orleans:ClusterId"]}\".");

        // --- pre-flight: nothing below touches a grain, so none of this warms anything. -----------
        var heavyIds = Enumerable.Range(0, HeavyDuels).Select(i => MatchId("heavy", i)).ToHashSet();
        var rows = await archive.ForPlayerAsync(HeavyId, 40);
        var gotRowIds = rows.Select(r => r.Id).ToHashSet();
        if (rows.Count != 40 || !gotRowIds.SetEquals(heavyIds))
            return Fail($"archive.ForPlayerAsync(\"{HeavyId}\", 40) returned {rows.Count} rows, of which " +
                $"{gotRowIds.Count(heavyIds.Contains)} of the 40 seeded ids were present. Seeding is incomplete " +
                "or was overwritten by something else — run \"bench-matches seed\" again before measuring.");
        Console.WriteLine("[bench] confirmed: the archive returns all 40 seeded rows for the heavy account.");

        await AssertGrainStateInRedisAsync(redis, "fresh", FreshDuels);
        await AssertGrainStateInRedisAsync(redis, "heavy", HeavyDuels);
        Console.WriteLine("[bench] confirmed: every seeded match (fresh and heavy) has grain state in Redis — " +
            "checked with a raw key scan, not through the grains, so this does not warm them.");

        // --- warm-up: JIT and connection pools, against the throwaway account only. ----------------
        Console.WriteLine("[bench] warm-up: 5 calls against the throwaway warmup account.");
        for (var i = 0; i < 5; i++)
            await TimedCallAsync(WarmupId, archive, players, grains, jsonOptions);

        Console.WriteLine("[bench] measurement order (fixed): fresh account, then heavy account.");
        var fresh = await MeasureAccountAsync("fresh", FreshId, FreshDuels, archive, players, grains, jsonOptions, expectedIds: null);
        var heavy = await MeasureAccountAsync("heavy", HeavyId, HeavyDuels, archive, players, grains, jsonOptions, expectedIds: heavyIds);

        var explain = await ExplainArchiveQueryAsync(mongoContext, HeavyId);
        PrintExplain(explain);

        var stack = await StackInfoAsync();
        var path = await MatchListReport.WriteAsync(fresh, heavy, explain, stack);
        Console.WriteLine($"[bench] wrote {path}");
        return 0;
    }

    private static async Task<AccountMeasurement> MeasureAccountAsync(string label, string meId, int seededDuels,
        IMatchArchive archive, IPlayerRepository players, IGrainFactory grains, JsonSerializerOptions jsonOptions,
        IReadOnlySet<string>? expectedIds)
    {
        // The cold first run: the first time this process ever activates this account's grains.
        var (cold, coldList) = await TimedCallAsync(meId, archive, players, grains, jsonOptions);
        if (expectedIds is not null)
        {
            var got = coldList.Select(m => m.Id).ToHashSet();
            var overlap = got.Count(expectedIds.Contains);
            if (overlap < expectedIds.Count)
                throw new InvalidOperationException($"[bench] the cold run for \"{label}\" returned {coldList.Count} " +
                    $"rows but only {overlap} of the {expectedIds.Count} seeded ids were in them.");
        }
        Console.WriteLine($"[bench] {label}: cold first run = {cold.TotalMs:F1}ms " +
            $"(archive {cold.ArchiveMs:F1}ms, fan-out {cold.GrainFanOutMs:F1}ms, players {cold.PlayersMs:F1}ms, " +
            $"json {cold.SerializeMs:F1}ms), rows={coldList.Count}");

        // Slowest individual grain call, for context on what the fan-out bucket is made of. This can
        // only be measured by calling grains one at a time outside GameEndpoints.ListMatchesAsync, so
        // it runs after the cold run above rather than during it — these grains are warm for it. The
        // cold fan-out number itself is bucket (b) of the cold run reported above, derived from the
        // real cold call.
        var slowestGrainMs = await SlowestIndividualGrainCallMsAsync(meId, archive, grains);

        var samples = new List<CallBuckets>(TimedRuns);
        for (var i = 0; i < TimedRuns; i++)
            samples.Add((await TimedCallAsync(meId, archive, players, grains, jsonOptions)).Buckets);

        var totals = samples.Select(s => s.TotalMs).Order().ToList();
        var p50 = Percentile(totals, 0.50);
        var p95 = Percentile(totals, 0.95);
        var max = totals[^1];

        // The run whose total is closest to p50 stands in for "the median run": one concrete set of
        // buckets to report and self-check, rather than four independently-medianed numbers that
        // never belonged to the same call.
        var median = samples.OrderBy(s => Math.Abs(s.TotalMs - p50)).First();
        var attributed = median.ArchiveMs + median.GrainFanOutMs + median.PlayersMs + median.SerializeMs;
        var unattributed = median.TotalMs - attributed;

        Console.WriteLine($"[bench] {label}: {TimedRuns} warm runs — p50={p50:F1}ms p95={p95:F1}ms max={max:F1}ms; " +
            $"median run: archive {median.ArchiveMs:F1}ms, fan-out {median.GrainFanOutMs:F1}ms, " +
            $"players {median.PlayersMs:F1}ms, json {median.SerializeMs:F1}ms, unattributed " +
            $"{unattributed:F1}ms ({(median.TotalMs == 0 ? 0 : unattributed / median.TotalMs * 100):F1}%); " +
            $"slowest individual grain call (warm) {slowestGrainMs:F1}ms");

        return new AccountMeasurement(label, seededDuels, cold, coldList.Count, median, unattributed,
            p50, p95, max, slowestGrainMs, samples.Count);
    }

    private static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        var index = (int)Math.Clamp(Math.Ceiling(p * sortedAscending.Count) - 1, 0, sortedAscending.Count - 1);
        return sortedAscending[index];
    }

    private static async Task<(CallBuckets Buckets, List<MatchSummaryDto> Result)> TimedCallAsync(string meId,
        IMatchArchive realArchive, IPlayerRepository realPlayers, IGrainFactory grains, JsonSerializerOptions jsonOptions)
    {
        var timingArchive = new TimingMatchArchive(realArchive);
        var timingPlayers = new TimingPlayerRepository(realPlayers);

        var callWatch = Stopwatch.StartNew();
        var list = await GameEndpoints.ListMatchesAsync(meId, activeOnly: false, take: null, timingArchive, timingPlayers, grains);
        callWatch.Stop();

        var jsonWatch = Stopwatch.StartNew();
        _ = JsonSerializer.SerializeToUtf8Bytes(list, jsonOptions);
        jsonWatch.Stop();

        var archiveMs = timingArchive.LastForPlayerElapsed.TotalMilliseconds;
        var playersMs = timingPlayers.LastGetManyElapsed.TotalMilliseconds;
        var fanOutMs = Math.Max(0, callWatch.Elapsed.TotalMilliseconds - archiveMs - playersMs);

        var buckets = new CallBuckets(archiveMs, fanOutMs, playersMs, jsonWatch.Elapsed.TotalMilliseconds,
            callWatch.Elapsed.TotalMilliseconds + jsonWatch.Elapsed.TotalMilliseconds);
        return (buckets, list);
    }

    /// <summary>
    /// Replicates only the fan-out step, outside <c>ListMatchesAsync</c>, so each grain call's own
    /// duration can be seen rather than just the wall clock of all of them together.
    /// </summary>
    private static async Task<double> SlowestIndividualGrainCallMsAsync(string meId, IMatchArchive archive, IGrainFactory grains)
    {
        var rows = await archive.ForPlayerAsync(meId, 40);
        if (rows.Count == 0) return 0;

        var elapsed = new double[rows.Count];
        await Task.WhenAll(rows.Select(async (r, i) =>
        {
            var sw = Stopwatch.StartNew();
            await grains.GetGrain<IMatchGrain>(r.Id).GetAsync(meId);
            elapsed[i] = sw.Elapsed.TotalMilliseconds;
        }));
        return elapsed.Max();
    }

    // ------------------------------------------------------------------------------------------
    // isolation, pre-flight checks and the explain plan
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Refuses to run against the default Mongo database or Orleans ClusterId, because seeding
    /// writes shared state (players, questions, the leaderboard) that a real deployment or another
    /// developer's dev stack could be using. Both are overridden the same way the app already reads
    /// them: Mongo__Database and Orleans__ClusterId.
    /// </summary>
    private static bool GuardIsolation(MongoOptions mongo, IConfiguration config, out string error)
    {
        if (mongo.Database == "quesshi")
        {
            error = "Refusing to run: Mongo:Database is \"quesshi\", the app's default database. Set " +
                "Mongo__Database to a dedicated name (e.g. \"quesshi_bench\") before seeding or measuring.";
            return false;
        }

        var clusterId = config["Orleans:ClusterId"];
        if (string.IsNullOrEmpty(clusterId) || clusterId == "quesshi")
        {
            error = "Refusing to run: Orleans:ClusterId is unset or \"quesshi\", the app's default cluster. Set " +
                "Orleans__ClusterId to a dedicated id (e.g. \"quesshi-bench\") before seeding or measuring.";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Checked with a raw key scan against Redis, not through <c>IMatchGrain.GetAsync</c> — going
    /// through the grain would activate it, and this must run before the cold run without warming it.
    /// </summary>
    private static async Task AssertGrainStateInRedisAsync(IConnectionMultiplexer redis, string prefix, int count)
    {
        var db = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var missing = new List<string>();

        for (var i = 0; i < count; i++)
        {
            var matchId = MatchId(prefix, i);
            var found = false;
            // The wildcard pattern only narrows candidates: unpadded ids mean "bench-heavy-m1" is a
            // substring of "bench-heavy-m10".."bench-heavy-m19" too, so every candidate key still
            // needs an exact-id check before it counts as this match's state.
            await foreach (var key in server.KeysAsync(db.Database, pattern: $"*{matchId}*", pageSize: 50))
            {
                if (!ContainsExactId((string)key!, matchId)) continue;
                found = true;
                break;
            }
            if (!found) missing.Add(matchId);
        }

        if (missing.Count > 0)
            throw new InvalidOperationException($"[bench] {missing.Count} of {count} seeded \"{prefix}\" matches " +
                $"have no grain state in Redis (database {db.Database}): {string.Join(", ", missing.Take(5))}" +
                $"{(missing.Count > 5 ? ", ..." : "")}. Run \"bench-matches seed\" again.");
    }

    /// <summary>
    /// True if <paramref name="id"/> occurs in <paramref name="key"/> as a whole token — not merely
    /// as a substring of a longer id. Guards against exactly the collision above: an unpadded id like
    /// "bench-heavy-m1" is a textual substring of "bench-heavy-m10", "bench-heavy-m11", etc., so a
    /// bare <c>Contains</c> would report a missing match as present whenever a numerically-adjacent
    /// one exists.
    /// </summary>
    internal static bool ContainsExactId(string key, string id)
    {
        var from = 0;
        while (true)
        {
            var idx = key.IndexOf(id, from, StringComparison.Ordinal);
            if (idx < 0) return false;

            var before = idx == 0 || !char.IsLetterOrDigit(key[idx - 1]);
            var afterPos = idx + id.Length;
            var after = afterPos >= key.Length || !char.IsLetterOrDigit(key[afterPos]);
            if (before && after) return true;

            from = idx + 1;
        }
    }

    /// <summary>
    /// Records the plan and execution statistics for the exact query <c>MongoMatchArchive.ForPlayerAsync</c>
    /// issues, against the 40-duel account. Recording only — nothing here changes the plan.
    /// </summary>
    private static async Task<ExplainResult> ExplainArchiveQueryAsync(MongoContext db, string playerId)
    {
        var filter = new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("ChallengerId", playerId),
            new BsonDocument("OpponentId", playerId)
        });

        var command = new BsonDocument
        {
            ["explain"] = new BsonDocument
            {
                ["find"] = "matches",
                ["filter"] = filter,
                ["sort"] = new BsonDocument("CreatedAt", -1),
                ["limit"] = 40
            },
            ["verbosity"] = "executionStats"
        };

        var result = await db.Matches.Database.RunCommandAsync<BsonDocument>(command);
        var stats = result["executionStats"].AsBsonDocument;
        var winningPlan = result["queryPlanner"]["winningPlan"].AsBsonDocument;

        return new ExplainResult(
            NReturned: stats["nReturned"].ToInt32(),
            KeysExamined: stats["totalKeysExamined"].ToInt32(),
            DocsExamined: stats["totalDocsExamined"].ToInt32(),
            HasBlockingSort: ContainsStage(winningPlan, "SORT"),
            WinningPlanStage: winningPlan.GetValue("stage", "?").AsString,
            Raw: result.ToJson());
    }

    private static bool ContainsStage(BsonDocument stage, string name)
    {
        if (stage.TryGetValue("stage", out var s) && s.IsString && s.AsString == name) return true;
        if (stage.TryGetValue("inputStage", out var input) && input.IsBsonDocument && ContainsStage(input.AsBsonDocument, name)) return true;
        if (stage.TryGetValue("inputStages", out var inputs) && inputs.IsBsonArray)
            foreach (var i in inputs.AsBsonArray)
                if (i.IsBsonDocument && ContainsStage(i.AsBsonDocument, name)) return true;
        return false;
    }

    private static void PrintExplain(ExplainResult explain) =>
        Console.WriteLine($"[bench] archive query explain: winning plan \"{explain.WinningPlanStage}\", " +
            $"nReturned={explain.NReturned}, keysExamined={explain.KeysExamined}, docsExamined={explain.DocsExamined}, " +
            $"blocking SORT stage: {(explain.HasBlockingSort ? "yes" : "no")}");

    private static async Task<StackInfo> StackInfoAsync()
    {
        var mongoDigest = await DockerDigestAsync("mongo:7");
        var redisDigest = await DockerDigestAsync("redis:7-alpine");
        var cpu = CpuModel();
        return new StackInfo(cpu, Environment.ProcessorCount, mongoDigest, redisDigest);
    }

    private static string CpuModel()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name"));
                if (line is not null) return line.Split(':', 2)[1].Trim();
            }
        }
        catch (IOException) { }
        return "unknown (record manually)";
    }

    private static async Task<string> DockerDigestAsync(string image)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("inspect");
            psi.ArgumentList.Add("--format={{index .RepoDigests 0}}");
            psi.ArgumentList.Add(image);
            using var proc = Process.Start(psi)!;
            var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 && output.Length > 0 ? output : $"{image}, digest unavailable ({image} not pulled locally? run docker inspect by hand)";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return $"{image}, digest unavailable (docker not on PATH here — run docker inspect by hand)";
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[bench] {message}");
        return 1;
    }
}

/// <summary>One call's elapsed time, split into the four suspects plus JSON serialisation.</summary>
internal readonly record struct CallBuckets(double ArchiveMs, double GrainFanOutMs, double PlayersMs, double SerializeMs, double TotalMs);

internal sealed record AccountMeasurement(string Label, int SeededDuels, CallBuckets Cold, int ColdRowCount,
    CallBuckets Median, double MedianUnattributedMs, double P50Ms, double P95Ms, double MaxMs,
    double SlowestGrainCallMs, int TimedRuns);

internal sealed record ExplainResult(int NReturned, int KeysExamined, int DocsExamined, bool HasBlockingSort,
    string WinningPlanStage, string Raw);

internal sealed record StackInfo(string Cpu, int ProcessorCount, string MongoImageDigest, string RedisImageDigest);
