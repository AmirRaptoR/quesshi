# What `GET /api/matches` costs (issue #4)

Generated 2026-09-01 06:27 UTC by `dotnet run --project src/Quesshi.Server -- bench-matches measure`, against real MongoDB and Redis (the stores from `compose.yaml`), after `bench-matches seed` ran as a separate process so this process's grains started cold.

**Measurement path: in-process.** The harness calls `GameEndpoints.ListMatchesAsync` directly with real store implementations, then serialises the returned `List<MatchSummaryDto>` itself with ASP.NET's configured `JsonSerializerOptions` (`DefaultIgnoreCondition = WhenWritingNull`, `src/Quesshi.Server/Program.cs:167-168`). End-to-end is that call plus that serialisation — nothing else is inside the timed interval.

**Request measured:** `active=false, take=null` — what `/duels` sends (`src/Quesshi.Web/Pages/Duels.razor:33`).

**Coldness:** `bench-matches seed` and `bench-matches measure` are separate processes (separate silos). The cold first run below is the first activation, in the measuring process, of that account's grains. Warm-up (5 calls) ran only against a third, throwaway account, never against "fresh" or "heavy". Measurement order was fixed: fresh, then heavy.

**Authentication:** not applicable. The in-process path calls `GameEndpoints.ListMatchesAsync` directly — there is no HTTP request and therefore nothing to authenticate.

## Machine and stack

- CPU: Intel Core Processor (Haswell, no TSX) (6 logical processors)
- Stores: containers (Docker), the same `mongo:7` / `redis:7-alpine` images `compose.yaml` pulls, run standalone on non-default ports rather than through `docker compose up`: this machine already had another `quesshi-mongo` / `quesshi-redis` pair running on the default ports for a different worktree, which is exactly the kind of database this harness must not touch.
- Mongo image digest: `mongo@sha256:b6421fd6d1c5ded6377b397d8983e2f82e2100dc5123332dcfda2065a472be5b`
- Redis image digest: `redis@sha256:ff02b58f971e7d7d156a1267e283fcbbeee91773b6aa36c49dac28ecfe28eadf`

## End-to-end elapsed time (ms)

| Account | Seeded duels | Cold first run | p50 (warm) | p95 (warm) | max (warm) | warm runs |
|---|---:|---:|---:|---:|---:|---:|
| fresh (2 duels) | 2 | 12.7 | 2.9 | 3.9 | 4.2 | 20 |
| heavy (40 duels) | 40 | 29.3 | 8.1 | 10.0 | 10.8 | 20 |

## Attribution across the four suspects

(a) the archive query `MongoMatchArchive.ForPlayerAsync`; (b) the grain fan-out — wall-clock of the `Task.WhenAll` at `GameEndpoints.cs:260-261`, derived as the residual after subtracting (a) and (c) from the call's own elapsed time; (c) the player lookup `MongoPlayerRepository.GetManyAsync`; (d) JSON serialisation, measured separately from the call. Because (b) is defined as that residual, (a)+(b)+(c) sum to the call's elapsed time by construction, and unattributed time here is mostly the small gap between stopping the call's stopwatch and starting the serialisation one, plus stopwatch overhead — not missing accounting.

### fresh

| | archive (a) | fan-out (b) | players (c) | json (d) | sum | end-to-end | unattributed |
|---|---:|---:|---:|---:|---:|---:|---:|
| cold first run | 2.2ms (17%) | 8.8ms (69%) | 1.7ms (13%) | 0.1ms (0%) | 12.7ms | 12.7ms | 0.0ms (0%) |
| median warm run | 1.3ms (45%) | 0.4ms (12%) | 1.1ms (39%) | 0.1ms (4%) | 2.9ms | 2.9ms | 0.0ms (0%) |

Self-check is not applied to the fresh account: its total is close enough to timer resolution that a percentage is not meaningful.

### heavy

| | archive (a) | fan-out (b) | players (c) | json (d) | sum | end-to-end | unattributed |
|---|---:|---:|---:|---:|---:|---:|---:|
| cold first run | 3.1ms (11%) | 23.9ms (82%) | 1.9ms (7%) | 0.4ms (1%) | 29.3ms | 29.3ms | 0.0ms (0%) |
| median warm run | 3.0ms (37%) | 3.3ms (41%) | 1.4ms (17%) | 0.4ms (5%) | 8.1ms | 8.1ms | 0.0ms (0%) |

Self-check (median run, heavy account, the criterion this applies to): unattributed 0.0ms of 8.1ms end-to-end. OK: 0.0% <= 15%.

## Grain fan-out detail

- Slowest individual grain call, fresh account (warm, satellite pass after the cold run): 2.5ms
- Slowest individual grain call, heavy account (warm, satellite pass after the cold run): 1.5ms
- Measured by timing each `IMatchGrain.GetAsync` call individually inside the same `Task.WhenAll` shape `ListMatchesAsync` uses, run once after the cold run (per account) so as not to warm the grains before the cold number was captured. These are therefore warm numbers, reported for context on what a concurrent fan-out's wall clock is made of, not as a cold figure in their own right.

## Archive query: `explain("executionStats")`

Query: `{ $or: [ {ChallengerId: <heavy id>}, {OpponentId: <heavy id>} ] }`, `sort: {CreatedAt: -1}, limit: 40`, against the 40-duel account, given the compound indexes at `src/Quesshi.Infrastructure/Mongo/MongoContext.cs:62-66`.

- Winning plan stage: `SUBPLAN`
- `nReturned`: 40
- `keysExamined`: 40
- `docsExamined`: 40
- Blocking `SORT` stage present: no

<details><summary>Raw explain output</summary>

```json
{ "explainVersion" : "1", "queryPlanner" : { "namespace" : "quesshi_bench.matches", "indexFilterSet" : false, "parsedQuery" : { "$or" : [{ "ChallengerId" : { "$eq" : "bench-heavy-me" } }, { "OpponentId" : { "$eq" : "bench-heavy-me" } }] }, "queryHash" : "635E34FA", "planCacheKey" : "FA10F3E0", "optimizationTimeMillis" : 0, "maxIndexedOrSolutionsReached" : false, "maxIndexedAndSolutionsReached" : false, "maxScansToExplodeReached" : false, "winningPlan" : { "stage" : "SUBPLAN", "inputStage" : { "stage" : "LIMIT", "limitAmount" : 40, "inputStage" : { "stage" : "FETCH", "inputStage" : { "stage" : "SORT_MERGE", "sortPattern" : { "CreatedAt" : -1 }, "inputStages" : [{ "stage" : "IXSCAN", "keyPattern" : { "ChallengerId" : 1, "CreatedAt" : -1 }, "indexName" : "ChallengerId_1_CreatedAt_-1", "isMultiKey" : false, "multiKeyPaths" : { "ChallengerId" : [], "CreatedAt" : [] }, "isUnique" : false, "isSparse" : false, "isPartial" : false, "indexVersion" : 2, "direction" : "forward", "indexBounds" : { "ChallengerId" : ["[\"bench-heavy-me\", \"bench-heavy-me\"]"], "CreatedAt" : ["[MaxKey, MinKey]"] } }, { "stage" : "IXSCAN", "keyPattern" : { "OpponentId" : 1, "CreatedAt" : -1 }, "indexName" : "OpponentId_1_CreatedAt_-1", "isMultiKey" : false, "multiKeyPaths" : { "OpponentId" : [], "CreatedAt" : [] }, "isUnique" : false, "isSparse" : false, "isPartial" : false, "indexVersion" : 2, "direction" : "forward", "indexBounds" : { "OpponentId" : ["[\"bench-heavy-me\", \"bench-heavy-me\"]"], "CreatedAt" : ["[MaxKey, MinKey]"] } }] } } } }, "rejectedPlans" : [] }, "executionStats" : { "executionSuccess" : true, "nReturned" : 40, "executionTimeMillis" : 0, "totalKeysExamined" : 40, "totalDocsExamined" : 40, "executionStages" : { "stage" : "SUBPLAN", "nReturned" : 40, "executionTimeMillisEstimate" : 0, "works" : 82, "advanced" : 40, "needTime" : 41, "needYield" : 0, "saveState" : 0, "restoreState" : 0, "isEOF" : 1, "inputStage" : { "stage" : "LIMIT", "nReturned" : 40, "executionTimeMillisEstimate" : 0, "works" : 81, "advanced" : 40, "needTime" : 41, "needYield" : 0, "saveState" : 0, "restoreState" : 0, "isEOF" : 1, "limitAmount" : 40, "inputStage" : { "stage" : "FETCH", "nReturned" : 40, "executionTimeMillisEstimate" : 0, "works" : 81, "advanced" : 40, "needTime" : 41, "needYield" : 0, "saveState" : 0, "restoreState" : 0, "isEOF" : 0, "docsExamined" : 40, "alreadyHasObj" : 0, "inputStage" : { "stage" : "SORT_MERGE", "nReturned" : 40, "executionTimeMillisEstimate" : 0, "works" : 81, "advanced" : 40, "needTime" : 41, "needYield" : 0, "saveState" : 0, "restoreState" : 0, "isEOF" : 0, "sortPattern" : { "CreatedAt" : -1 }, "dupsTested" : 40, "dupsDropped" : 0, "inputStages" : [{ "stage" : "IXSCAN", "nReturned" : 40, "executionTimeMillisEstimate" : 0, "works" : 40, "advanced" : 40, "needTime" : 0, "needYield" : 0, "saveState" : 0, "restoreState" : 0, "isEOF" : 0, "keyPattern" : { "ChallengerId" : 1, "CreatedAt" : -1 }, "indexName" : "ChallengerId_1_CreatedAt_-1", "isMultiKey" : false, "multiKeyPaths" : { "ChallengerId" : [], "CreatedAt" : [] }, "isUnique" : false, "isSparse" : false, "isPartial" : false, "indexVersion" : 2, "direction" : "forward", "indexBounds" : { "ChallengerId" : ["[\"bench-heavy-me\", \"bench-heavy-me\"]"], "CreatedAt" : ["[MaxKey, MinKey]"] }, "keysExamined" : 40, "seeks" : 1, "dupsTested" : 0, "dupsDropped" : 0 }, { "stage" : "IXSCAN", "nReturned" : 0, "executionTimeMillisEstimate" : 0, "works" : 1, "advanced" : 0, "needTime" : 0, "needYield" : 0, "saveState" : 0, "restoreState" : 0, "isEOF" : 1, "keyPattern" : { "OpponentId" : 1, "CreatedAt" : -1 }, "indexName" : "OpponentId_1_CreatedAt_-1", "isMultiKey" : false, "multiKeyPaths" : { "OpponentId" : [], "CreatedAt" : [] }, "isUnique" : false, "isSparse" : false, "isPartial" : false, "indexVersion" : 2, "direction" : "forward", "indexBounds" : { "OpponentId" : ["[\"bench-heavy-me\", \"bench-heavy-me\"]"], "CreatedAt" : ["[MaxKey, MinKey]"] }, "keysExamined" : 0, "seeks" : 1, "dupsTested" : 0, "dupsDropped" : 0 }] } } } } }, "command" : { "find" : "matches", "filter" : { "$or" : [{ "ChallengerId" : "bench-heavy-me" }, { "OpponentId" : "bench-heavy-me" }] }, "sort" : { "CreatedAt" : -1 }, "limit" : 40, "$db" : "quesshi_bench" }, "serverInfo" : { "host" : "4c6b8e55a234", "port" : 27017, "version" : "7.0.40", "gitVersion" : "37741b538c02da076d1e34d66113ac0230d4523a" }, "serverParameters" : { "internalQueryFacetBufferSizeBytes" : 104857600, "internalQueryFacetMaxOutputDocSizeBytes" : 104857600, "internalLookupStageIntermediateDocumentMaxSizeBytes" : 104857600, "internalDocumentSourceGroupMaxMemoryBytes" : 104857600, "internalQueryMaxBlockingSortMemoryUsageBytes" : 104857600, "internalQueryProhibitBlockingMergeOnMongoS" : 0, "internalQueryMaxAddToSetBytes" : 104857600, "internalDocumentSourceSetWindowFieldsMaxMemoryBytes" : 104857600, "internalQueryFrameworkControl" : "forceClassicEngine" }, "ok" : 1.0 }
```

</details>

## Reproducing this

```bash
docker compose up -d mongo redis   # or point at a dedicated instance — see below

export ConnectionStrings__Mongo=mongodb://localhost:27017
export Mongo__Database=quesshi_bench          # never the default "quesshi"
export ConnectionStrings__Redis=localhost:6379,defaultDatabase=1   # a dedicated index
export Orleans__ClusterId=quesshi-bench        # never the default "quesshi"
export ASPNETCORE_URLS=http://127.0.0.1:0      # ephemeral port; HTTP is not used

dotnet run --project src/Quesshi.Server -- bench-matches seed      # writes the accounts, exits
dotnet run --project src/Quesshi.Server -- bench-matches measure   # a fresh process; times it
```

### Isolation

- `Mongo__Database` and `Orleans__ClusterId` must both be set away from their defaults — `bench-matches` refuses to run otherwise. Seeding writes players, questions and match history; a shared default database means writing into whatever a developer or a deployment is using.
- `RedisLeaderboard` writes to the fixed key `quesshi:leaderboard` (`src/Quesshi.Infrastructure/Redis/RedisLeaderboard.cs:9`), which no `ClusterId` namespaces. Orleans clustering, grain storage and reminders go through the same `ConnectionStrings__Redis` connection too. The straightforward fix is a dedicated Redis database index (`,defaultDatabase=N` in the connection string, as above) or a dedicated Redis instance — not the default index 0 a real deployment uses.
- **Do not point this at a database anyone is using** — a developer's local stack, a staging environment, or production. Use a dedicated database name, a dedicated Redis index or instance, and a dedicated ClusterId, every time.
- To undo a run against a Redis index used only for this: `redis-cli -n <index> FLUSHDB`. The Mongo side is undone by dropping the dedicated database.
