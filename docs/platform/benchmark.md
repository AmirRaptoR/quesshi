# Live-path benchmark

This is the measured result of the batched live answer path (architecture
§7.4.1) and the source of the selected live player maximum. The complete
machine-readable result, including the source revision, sample counts,
p50/p95/p99/max values, burst drain, receipt coverage, on-time refusals,
failure causes, GC activity and the per-scenario budget assessment, is
[`benchmark.raw.json`](benchmark.raw.json). Its schema and budget classification
are validated by `BudgetClassificationTests` in `platform/bench/Quesshi.Bench.Live.Tests`.

Selected live maximum: **2** players.

## Reproducing the run

Source revision `cf0e94be1d8488e927bde85c7641548fd45caa1a` (the optimized code; later commits change
only this report), run at 2026-10-22 03:56 UTC:

```text
dotnet run -c Release --project platform/bench/Quesshi.Bench.Live -- --matrix --repetitions 2 --allow-disposable-postgres
```

Host `vps-c4d96c06`: Intel Core Processor (Haswell, no TSX), 6 logical processors,
Ubuntu 26.04.1 LTS, .NET 10.0.13. PostgreSQL 16.4 ran
locally (local PostgreSQL at localhost:54329) as a disposable server; the explicit switch
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
refusals and receipt gaps are sums). Drain is the last accepted answer's commit
relative to the question deadline (negative is before it). Budgets: ordinary
answer < 2, round close < 50, finish < 100, drain <= 2000, zero on-time
refusals, zero commands without a server receipt. `—` is not produced by the
scenario (spread has no burst; cold stops after the first slot).

| Players | Scenario | Runs | Ordinary max | Close max | Finish max | Drain | On-time refusals | No receipt | Accepted (min) | Budgets |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 2 | burst | 2 | 0.6 | 15.5 | 5.8 | -18.4 | 0 | 0 | 2/2 | pass |
| 20 | burst | 2 | 0.7 | 91.9 | 11.0 | 73.2 | 0 | 0 | 20/20 | FAIL |
| 50 | burst | 2 | 0.8 | 19.9 | 11.4 | -26.3 | 0 | 0 | 50/50 | pass |
| 100 | burst | 2 | 5.2 | 25.2 | 33.5 | -11.9 | 0 | 0 | 100/100 | FAIL |
| 150 | burst | 2 | 7.2 | 37.4 | 12.5 | -3.6 | 0 | 0 | 150/150 | FAIL |
| 200 | burst | 2 | 5.3 | 60.6 | 21.9 | 15.5 | 0 | 0 | 200/200 | FAIL |
| 250 | burst | 2 | 10.9 | 85.1 | 25.1 | 66.7 | 0 | 0 | 250/250 | FAIL |
| 350 | burst | 2 | 54.6 | 61.5 | 26.4 | 253.7 | 0 | 0 | 350/350 | FAIL |
| 500 | burst | 2 | 101.0 | 68.4 | 44.3 | 473.0 | 0 | 0 | 500/500 | FAIL |
| 2 | spread | 2 | 0.3 | 8.0 | 5.2 | — | 0 | 0 | 2/2 | pass |
| 20 | spread | 2 | 0.3 | 7.7 | 6.0 | — | 0 | 0 | 20/20 | pass |
| 50 | spread | 2 | 5.3 | 36.1 | 8.5 | — | 0 | 0 | 50/50 | FAIL |
| 100 | spread | 2 | 13.1 | 18.1 | 14.1 | — | 0 | 0 | 100/100 | FAIL |
| 150 | spread | 2 | 0.8 | 15.7 | 13.5 | — | 0 | 0 | 150/150 | pass |
| 200 | spread | 2 | 6.3 | 22.5 | 21.8 | — | 0 | 0 | 200/200 | FAIL |
| 250 | spread | 2 | 11.9 | 24.7 | 22.6 | — | 0 | 0 | 250/250 | FAIL |
| 350 | spread | 2 | 14.8 | 32.6 | 31.7 | — | 0 | 0 | 350/350 | FAIL |
| 500 | spread | 2 | 12.9 | 45.7 | 39.5 | — | 0 | 0 | 500/500 | FAIL |
| 2 | burst-streams | 2 | 0.4 | 8.9 | 7.6 | -30.5 | 0 | 0 | 2/2 | pass |
| 20 | burst-streams | 2 | 5.3 | 55.2 | 6.5 | 33.3 | 0 | 0 | 20/20 | FAIL |
| 50 | burst-streams | 2 | 2.1 | 125.1 | 9.5 | 99.4 | 0 | 0 | 50/50 | FAIL |
| 100 | burst-streams | 2 | 24.6 | 22.6 | 12.0 | -18.7 | 0 | 0 | 100/100 | FAIL |
| 150 | burst-streams | 2 | 12.7 | 28.0 | 11.8 | 8.0 | 0 | 0 | 150/150 | FAIL |
| 200 | burst-streams | 2 | 5.2 | 59.1 | 19.2 | 29.5 | 0 | 0 | 200/200 | FAIL |
| 250 | burst-streams | 2 | 24.7 | 100.6 | 20.7 | 83.8 | 0 | 0 | 250/250 | FAIL |
| 350 | burst-streams | 2 | 106.9 | 57.5 | 27.6 | 396.1 | 0 | 0 | 350/350 | FAIL |
| 500 | burst-streams | 2 | 59.1 | 57.0 | 38.2 | 675.1 | 0 | 0 | 500/500 | FAIL |
| 2 | cold | 2 | 1.5 | 10.0 | — | -30.1 | 0 | 0 | 2/2 | pass |
| 20 | cold | 2 | 0.2 | 8.4 | — | -38.4 | 0 | 0 | 20/20 | pass |
| 50 | cold | 2 | 1.0 | 13.0 | — | -32.7 | 0 | 0 | 50/50 | pass |
| 100 | cold | 2 | 1.4 | 20.4 | — | -18.9 | 0 | 0 | 100/100 | pass |
| 150 | cold | 2 | 2.3 | 29.0 | — | -15.9 | 0 | 0 | 150/150 | FAIL |
| 200 | cold | 2 | 56.7 | 46.4 | — | 134.9 | 0 | 0 | 200/200 | FAIL |
| 250 | cold | 2 | 26.0 | 76.2 | — | 56.0 | 0 | 0 | 250/250 | FAIL |
| 350 | cold | 2 | 66.3 | 54.1 | — | 225.5 | 0 | 0 | 350/350 | FAIL |
| 500 | cold | 2 | 80.4 | 53.7 | — | 522.6 | 0 | 0 | 500/500 | FAIL |
| 2 | pool10 | 2 | 1.4 | 8.5 | 5.0 | -34.7 | 0 | 0 | 2/2 | pass |
| 20 | pool10 | 2 | 0.8 | 10.7 | 12.4 | -33.4 | 0 | 0 | 20/20 | pass |
| 50 | pool10 | 2 | 0.6 | 18.0 | 10.1 | -26.4 | 0 | 0 | 50/50 | pass |
| 100 | pool10 | 2 | 14.9 | 29.8 | 14.6 | -17.9 | 0 | 0 | 100/100 | FAIL |
| 150 | pool10 | 2 | 23.0 | 41.1 | 16.6 | -1.2 | 0 | 0 | 150/150 | FAIL |
| 200 | pool10 | 2 | 10.2 | 40.7 | 19.0 | 6.1 | 0 | 0 | 200/200 | FAIL |
| 250 | pool10 | 2 | 21.5 | 96.7 | 26.9 | 69.7 | 0 | 0 | 250/250 | FAIL |
| 350 | pool10 | 2 | 34.7 | 68.9 | 34.4 | 256.5 | 0 | 0 | 350/350 | FAIL |
| 500 | pool10 | 2 | 108.4 | 75.7 | 40.1 | 606.0 | 0 | 0 | 500/500 | FAIL |

## Analysis

Every command in every run was accepted: no count produced an on-time refusal,
a timeout or a command without a receipt, and the burst drained 675 ms
after the deadline at most at 500 players (budget: 2000 ms). The earlier baseline
(one transaction per answer) accepted 46 of 500 burst answers and refused the
rest; batching removes that failure. The drain, refusal and finish budgets
pass at every tested count.

The limiting checks are the two per-turn maxima, which are single worst
samples and therefore sensitive to host stalls: the ordinary-answer
maximum (< 2 ms) and the round close (< 50 ms). At 2 players both are well
inside budget in every scenario. From 20 players some run has a single turn
over budget; several of those (for example a 92 ms close at 20 burst players with
no garbage collection during the run) coincide with no GC pause and are scheduler
stalls of the shared virtual machine rather than engine work, while at 100
players and above the ordinary-answer maximum is over budget in most scenarios.

An earlier full run of the same matrix on the previous revision (identical
batching logic, timing taken with `Stopwatch` instead of the injected clock) on
the same host selected 50 players: 2, 20 and 50 passed every check and 100 and
above failed. The two runs agree that 100 and above fail and that 2 passes, and
disagree about 20 and 50, which shows the strict worst-sample budgets at those
counts are inside the noise of this host. This report publishes only the run of
the final code, and the conservative reading of both runs is the same: the
only count that passed in every run is 2.

**2 is the selected maximum**: the largest tested count that, together
with every smaller tested count, met every applicable budget in every scenario
and repetition. The tested counts above it were rejected for these checks:

| Players | Passed | Failed checks |
| ---: | --- | --- |
| 2 | yes | — |
| 20 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:round_close_max |
| 50 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, spread:ordinary_answer_max |
| 100 | no | burst-streams:ordinary_answer_max, burst:ordinary_answer_max, pool10:ordinary_answer_max, spread:ordinary_answer_max |
| 150 | no | burst-streams:ordinary_answer_max, burst:ordinary_answer_max, cold:ordinary_answer_max, pool10:ordinary_answer_max |
| 200 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, pool10:ordinary_answer_max, spread:ordinary_answer_max |
| 250 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 350 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 500 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |

Counts between the tested ones were not measured and are not claimed. The
maximum is a result for this host, not a guarantee for other hardware. A
higher cap needs the same matrix re-run on dedicated hardware (or with more
repetitions) where host stalls do not dominate the worst-sample budgets.
