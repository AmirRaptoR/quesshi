# Live-path benchmark

This is the measured result of the batched live answer path (architecture
§7.4.1) and the source of the selected live player maximum. The complete
machine-readable result, including the source revision, sample counts,
p50/p95/p99/max values, burst drain, receipt coverage, on-time refusals,
failure causes, GC activity and the per-scenario budget assessment, is
[`benchmark.raw.json`](benchmark.raw.json). Its schema and budget classification
are validated by `BudgetClassificationTests` in `platform/bench/Quesshi.Bench.Live.Tests`.

Selected live maximum: **20** players.

## Reproducing the run

Source revision `2594b10f6cc4084e2d121e4bbb38b03b18d79d83` (the optimized code; later commits change
only this report), run at 2026-10-22 07:51 UTC:

```text
dotnet run -c Release --project platform/bench/Quesshi.Bench.Live -- --matrix --repetitions 2 --allow-disposable-postgres
```

Host `vps-c4d96c06`: Intel Core Processor (Haswell, no TSX), 6 logical processors,
Ubuntu 26.04.1 LTS, .NET 10.0.13. PostgreSQL 16.4 ran
locally (local PostgreSQL at 127.0.0.1:54329) as a disposable server; the explicit switch
acknowledges that the runner provisions benchmark roles and databases there.
Each case uses two independently hosted Orleans silos and one PostgreSQL
database. Players run in a separate load process, authenticate as guests, send
answers over the production HTTP command endpoint, and the `burst-streams`
case also consumes the production SSE endpoint. The load process, both silos and
PostgreSQL share one virtual machine with CPU steal, so the figures include
host scheduling stalls that a dedicated deployment would not have.

Counts: 2, 20, 50, 100, 150, 200, 250, 350 and 500 players, five scenarios,
two repetitions each (90 runs). `burst` schedules answers in the final 250 ms,
`spread` across the question, `burst-streams` adds 500 open SSE consumers, `cold`
activates from 100 persisted items, `pool10` is a burst with a 10-connection
application pool.

## Results

All values are milliseconds and are the worst of the repetitions (maxima;
refusals and receipt gaps are sums). Ordinary is the grain's in-memory turn for an
answer; commit wait is the time from an answer's acceptance to its batch's commit
(§7.4.1). Drain is the last accepted answer's commit relative to the question
deadline (negative is before it). Budgets: ordinary answer < 2, commit wait < 2000,
round close < 50, finish < 100, drain <= 2000, zero on-time refusals, zero commands
without a server receipt. `—` is not produced by the scenario (spread has no burst;
cold stops after the first slot).

| Players | Scenario | Runs | Ordinary max | Commit wait max | Close max | Finish max | Drain | On-time refusals | No receipt | Accepted (min) | Budgets |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 2 | burst | 2 | 0.6 | 11.2 | 14.7 | 8.4 | -11.6 | 0 | 0 | 2/2 | pass |
| 20 | burst | 2 | 0.9 | 48.6 | 13.2 | 7.8 | -31.1 | 0 | 0 | 20/20 | pass |
| 50 | burst | 2 | 0.5 | 39.8 | 26.9 | 15.4 | -11.9 | 0 | 0 | 50/50 | pass |
| 100 | burst | 2 | 3.8 | 70.7 | 47.4 | 23.9 | 25.4 | 0 | 0 | 100/100 | FAIL |
| 150 | burst | 2 | 3.1 | 82.9 | 50.1 | 37.0 | 11.0 | 0 | 0 | 150/150 | FAIL |
| 200 | burst | 2 | 23.2 | 150.5 | 70.6 | 23.6 | 28.9 | 0 | 0 | 200/200 | FAIL |
| 250 | burst | 2 | 65.5 | 238.4 | 61.3 | 26.7 | 149.9 | 0 | 0 | 250/250 | FAIL |
| 350 | burst | 2 | 63.9 | 295.6 | 73.9 | 43.4 | 271.7 | 0 | 0 | 350/350 | FAIL |
| 500 | burst | 2 | 135.3 | 365.0 | 76.2 | 41.5 | 591.4 | 0 | 0 | 500/500 | FAIL |
| 2 | spread | 2 | 0.3 | 9.6 | 8.9 | 5.8 | — | 0 | 0 | 2/2 | pass |
| 20 | spread | 2 | 0.4 | 18.2 | 7.5 | 7.3 | — | 0 | 0 | 20/20 | pass |
| 50 | spread | 2 | 1.1 | 31.1 | 21.9 | 16.0 | — | 0 | 0 | 50/50 | pass |
| 100 | spread | 2 | 2.0 | 66.5 | 32.5 | 23.3 | — | 0 | 0 | 100/100 | pass |
| 150 | spread | 2 | 8.3 | 31.3 | 20.1 | 20.4 | — | 0 | 0 | 150/150 | FAIL |
| 200 | spread | 2 | 1.3 | 28.5 | 33.2 | 18.2 | — | 0 | 0 | 200/200 | pass |
| 250 | spread | 2 | 12.0 | 20.7 | 21.5 | 22.3 | — | 0 | 0 | 250/250 | FAIL |
| 350 | spread | 2 | 8.2 | 36.0 | 36.9 | 44.1 | — | 0 | 0 | 350/350 | FAIL |
| 500 | spread | 2 | 6.9 | 48.8 | 50.0 | 44.2 | — | 0 | 0 | 500/500 | FAIL |
| 2 | burst-streams | 2 | 0.2 | 8.3 | 9.2 | 7.0 | -31.0 | 0 | 0 | 2/2 | pass |
| 20 | burst-streams | 2 | 0.3 | 17.9 | 7.4 | 11.5 | -34.5 | 0 | 0 | 20/20 | pass |
| 50 | burst-streams | 2 | 6.2 | 73.8 | 89.6 | 24.3 | 50.9 | 0 | 0 | 50/50 | FAIL |
| 100 | burst-streams | 2 | 2.3 | 64.4 | 22.5 | 13.8 | -16.9 | 0 | 0 | 100/100 | FAIL |
| 150 | burst-streams | 2 | 3.0 | 72.7 | 45.6 | 21.3 | -0.5 | 0 | 0 | 150/150 | FAIL |
| 200 | burst-streams | 2 | 24.8 | 127.0 | 76.5 | 18.3 | 46.8 | 0 | 0 | 200/200 | FAIL |
| 250 | burst-streams | 2 | 86.7 | 220.2 | 99.9 | 24.9 | 201.6 | 0 | 0 | 250/250 | FAIL |
| 350 | burst-streams | 2 | 149.6 | 310.3 | 64.0 | 32.4 | 311.7 | 0 | 0 | 350/350 | FAIL |
| 500 | burst-streams | 2 | 194.5 | 332.5 | 57.1 | 49.8 | 689.8 | 0 | 0 | 500/500 | FAIL |
| 2 | cold | 2 | 0.3 | 8.9 | 7.7 | — | -32.4 | 0 | 0 | 2/2 | pass |
| 20 | cold | 2 | 0.2 | 30.7 | 21.8 | — | -16.3 | 0 | 0 | 20/20 | pass |
| 50 | cold | 2 | 11.5 | 113.3 | 64.0 | — | 49.9 | 0 | 0 | 50/50 | FAIL |
| 100 | cold | 2 | 7.5 | 92.8 | 41.3 | — | 8.6 | 0 | 0 | 100/100 | FAIL |
| 150 | cold | 2 | 2.7 | 70.7 | 40.1 | — | -5.5 | 0 | 0 | 150/150 | FAIL |
| 200 | cold | 2 | 20.8 | 110.0 | 54.6 | — | 33.4 | 0 | 0 | 200/200 | FAIL |
| 250 | cold | 2 | 24.8 | 163.7 | 93.4 | — | 62.2 | 0 | 0 | 250/250 | FAIL |
| 350 | cold | 2 | 104.0 | 302.5 | 195.0 | — | 285.3 | 0 | 0 | 350/350 | FAIL |
| 500 | cold | 2 | 121.8 | 356.6 | 72.8 | — | 547.4 | 0 | 0 | 500/500 | FAIL |
| 2 | pool10 | 2 | 0.8 | 9.9 | 15.5 | 6.6 | -4.8 | 0 | 0 | 2/2 | pass |
| 20 | pool10 | 2 | 0.2 | 21.2 | 17.9 | 8.0 | -26.3 | 0 | 0 | 20/20 | pass |
| 50 | pool10 | 2 | 0.8 | 41.9 | 18.1 | 15.7 | -27.2 | 0 | 0 | 50/50 | pass |
| 100 | pool10 | 2 | 19.8 | 58.6 | 37.4 | 22.4 | 0.5 | 0 | 0 | 100/100 | FAIL |
| 150 | pool10 | 2 | 2.4 | 67.3 | 36.4 | 16.1 | -0.9 | 0 | 0 | 150/150 | FAIL |
| 200 | pool10 | 2 | 20.1 | 142.9 | 54.3 | 22.1 | 33.3 | 0 | 0 | 200/200 | FAIL |
| 250 | pool10 | 2 | 27.8 | 198.2 | 74.7 | 25.1 | 33.1 | 0 | 0 | 250/250 | FAIL |
| 350 | pool10 | 2 | 75.0 | 284.9 | 41.1 | 35.5 | 236.7 | 0 | 0 | 350/350 | FAIL |
| 500 | pool10 | 2 | 112.8 | 349.6 | 75.8 | 48.4 | 567.1 | 0 | 0 | 500/500 | FAIL |

## Analysis

Every command in every run was accepted: no count produced an on-time refusal,
a timeout or a command without a receipt (0 in total), the burst drained
690 ms after the deadline at most (budget: 2000 ms), and no answer waited
more than 365 ms for its commit (budget: 2000 ms). The earlier baseline (one
transaction per answer) accepted 46 of 500 burst answers and refused the rest;
batching removes that failure. The drain, commit-wait, refusal, receipt and finish
budgets pass at every tested count.

The limiting checks are the two per-turn maxima, which are single worst
samples and therefore sensitive to host stalls: the ordinary-answer maximum
(< 2 ms, the in-memory turn) and the round close (< 50 ms). Both are within budget
in every scenario and repetition at 2 and 20 players. From 50 players some run has a
single turn over budget, and from 100 players the ordinary-answer maximum is over
budget in most scenarios.

Including the batch's commit in the ordinary-answer turn was also measured (an equal
share of the batch transaction added to each answer). A PostgreSQL commit alone takes
several milliseconds on this host, so no count, not even 2, met the 2 ms budget on
that definition. §7.4 therefore defines the ordinary-answer budget as the grain's
occupied time, because the grain is free during a group commit, and bounds the write
through the commit-wait budget above.

Earlier full runs of the same matrix on previous revisions on this host selected 50 and
2 players. The runs agree that 100 and above fail and that 2 passes, and disagree
about 20 and 50, which shows that the strict worst-sample budgets at those counts are
inside the noise of this host. This report publishes the run of the final code.

**20 is the selected maximum**: the largest tested count that, together
with every smaller tested count, met every applicable budget in every scenario
and repetition, with zero on-time refusals. The tested counts above it were rejected for these checks:

| Players | Passed | Failed checks |
| ---: | --- | --- |
| 2 | yes | — |
| 20 | yes | — |
| 50 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, cold:ordinary_answer_max, cold:round_close_max |
| 100 | no | burst-streams:ordinary_answer_max, burst:ordinary_answer_max, cold:ordinary_answer_max, pool10:ordinary_answer_max |
| 150 | no | burst-streams:ordinary_answer_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, pool10:ordinary_answer_max, spread:ordinary_answer_max |
| 200 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max |
| 250 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 350 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, spread:ordinary_answer_max |
| 500 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |

Counts between the tested ones were not measured and are not claimed. The
maximum is a result for this host, not a guarantee for other hardware. A
higher cap needs the same matrix re-run on dedicated hardware (or with more
repetitions) where host stalls do not dominate the worst-sample budgets.
