# Live-path benchmark

This is the measured result of the batched live answer path (architecture
§7.4.1) and the source of the selected live player maximum. The complete
machine-readable result, including the source revision, sample counts,
p50/p95/p99/max values, burst drain, receipt coverage, on-time refusals,
failure causes, GC activity and the per-scenario budget assessment, is
[`benchmark.raw.json`](benchmark.raw.json). Its schema and budget classification
are validated by `BudgetClassificationTests` in `platform/bench/Quesshi.Bench.Live.Tests`.

Selected live maximum: **50** players.

## Reproducing the run

Source revision `6640c9223be081f4453aa55104b37c033ad6158c` (the optimized code; later commits change
only this report), run at 2026-10-22 02:47 UTC:

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
PostgreSQL share the one host, so the figures are conservative for a
production deployment with separate machines.

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
| 2 | burst | 2 | 0.5 | 7.5 | 12.4 | -29.6 | 0 | 0 | 2/2 | pass |
| 20 | burst | 2 | 0.7 | 10.9 | 6.6 | -36.6 | 0 | 0 | 20/20 | pass |
| 50 | burst | 2 | 0.5 | 20.4 | 11.9 | -24.1 | 0 | 0 | 50/50 | pass |
| 100 | burst | 2 | 2.0 | 27.4 | 13.3 | -13.2 | 0 | 0 | 100/100 | pass |
| 150 | burst | 2 | 21.7 | 38.6 | 16.2 | -5.3 | 0 | 0 | 150/150 | FAIL |
| 200 | burst | 2 | 2.6 | 45.1 | 20.3 | 0.2 | 0 | 0 | 200/200 | FAIL |
| 250 | burst | 2 | 27.2 | 74.8 | 25.6 | 29.3 | 0 | 0 | 250/250 | FAIL |
| 350 | burst | 2 | 150.6 | 69.7 | 34.5 | 282.3 | 0 | 0 | 350/350 | FAIL |
| 500 | burst | 2 | 73.7 | 67.7 | 40.0 | 470.1 | 0 | 0 | 500/500 | FAIL |
| 2 | spread | 2 | 0.3 | 14.3 | 4.8 | — | 0 | 0 | 2/2 | pass |
| 20 | spread | 2 | 0.2 | 8.2 | 6.2 | — | 0 | 0 | 20/20 | pass |
| 50 | spread | 2 | 0.3 | 14.7 | 10.0 | — | 0 | 0 | 50/50 | pass |
| 100 | spread | 2 | 0.9 | 16.8 | 16.5 | — | 0 | 0 | 100/100 | pass |
| 150 | spread | 2 | 0.7 | 15.2 | 16.5 | — | 0 | 0 | 150/150 | pass |
| 200 | spread | 2 | 6.0 | 18.9 | 17.0 | — | 0 | 0 | 200/200 | FAIL |
| 250 | spread | 2 | 6.9 | 19.9 | 22.2 | — | 0 | 0 | 250/250 | FAIL |
| 350 | spread | 2 | 11.3 | 32.7 | 43.1 | — | 0 | 0 | 350/350 | FAIL |
| 500 | spread | 2 | 15.4 | 47.5 | 43.7 | — | 0 | 0 | 500/500 | FAIL |
| 2 | burst-streams | 2 | 0.2 | 7.2 | 6.3 | -32.2 | 0 | 0 | 2/2 | pass |
| 20 | burst-streams | 2 | 0.2 | 9.0 | 8.1 | -31.9 | 0 | 0 | 20/20 | pass |
| 50 | burst-streams | 2 | 0.4 | 12.5 | 7.5 | -16.6 | 0 | 0 | 50/50 | pass |
| 100 | burst-streams | 2 | 3.6 | 31.6 | 11.3 | -7.0 | 0 | 0 | 100/100 | FAIL |
| 150 | burst-streams | 2 | 16.3 | 25.8 | 14.7 | 9.5 | 0 | 0 | 150/150 | FAIL |
| 200 | burst-streams | 2 | 89.5 | 58.8 | 16.5 | 134.8 | 0 | 0 | 200/200 | FAIL |
| 250 | burst-streams | 2 | 23.7 | 124.0 | 26.4 | 91.2 | 0 | 0 | 250/250 | FAIL |
| 350 | burst-streams | 2 | 138.5 | 47.1 | 25.5 | 312.2 | 0 | 0 | 350/350 | FAIL |
| 500 | burst-streams | 2 | 114.0 | 63.0 | 42.0 | 626.0 | 0 | 0 | 500/500 | FAIL |
| 2 | cold | 2 | 0.3 | 9.1 | — | -33.9 | 0 | 0 | 2/2 | pass |
| 20 | cold | 2 | 0.3 | 11.7 | — | -34.0 | 0 | 0 | 20/20 | pass |
| 50 | cold | 2 | 0.3 | 24.8 | — | -20.0 | 0 | 0 | 50/50 | pass |
| 100 | cold | 2 | 2.3 | 26.3 | — | -15.3 | 0 | 0 | 100/100 | FAIL |
| 150 | cold | 2 | 3.1 | 34.4 | — | -8.1 | 0 | 0 | 150/150 | FAIL |
| 200 | cold | 2 | 23.3 | 66.6 | — | 26.6 | 0 | 0 | 200/200 | FAIL |
| 250 | cold | 2 | 69.0 | 73.4 | — | 130.3 | 0 | 0 | 250/250 | FAIL |
| 350 | cold | 2 | 101.2 | 54.7 | — | 216.5 | 0 | 0 | 350/350 | FAIL |
| 500 | cold | 2 | 68.7 | 45.1 | — | 500.2 | 0 | 0 | 500/500 | FAIL |
| 2 | pool10 | 2 | 1.7 | 6.7 | 4.8 | -34.2 | 0 | 0 | 2/2 | pass |
| 20 | pool10 | 2 | 0.3 | 6.4 | 9.5 | -41.0 | 0 | 0 | 20/20 | pass |
| 50 | pool10 | 2 | 0.8 | 11.0 | 8.9 | -33.2 | 0 | 0 | 50/50 | pass |
| 100 | pool10 | 2 | 14.1 | 20.7 | 14.1 | -22.8 | 0 | 0 | 100/100 | FAIL |
| 150 | pool10 | 2 | 18.8 | 33.5 | 14.3 | -12.0 | 0 | 0 | 150/150 | FAIL |
| 200 | pool10 | 2 | 23.0 | 76.8 | 23.6 | 44.6 | 0 | 0 | 200/200 | FAIL |
| 250 | pool10 | 2 | 14.5 | 85.0 | 22.8 | 61.5 | 0 | 0 | 250/250 | FAIL |
| 350 | pool10 | 2 | 66.6 | 61.5 | 36.5 | 258.6 | 0 | 0 | 350/350 | FAIL |
| 500 | pool10 | 2 | 86.1 | 69.5 | 38.6 | 505.0 | 0 | 0 | 500/500 | FAIL |

## Analysis

Every command in every run was accepted: no count produced an on-time refusal,
a timeout or a command without a receipt. The earlier baseline (one transaction
per answer) accepted 46 of 500 burst answers and refused the rest; batching
removes that failure entirely, and the burst drains within roughly 0.65 s of the
deadline at 500 players (budget: 2 s).

What the batching did not remove is the per-turn cost on a shared six-processor
host. The two per-turn budgets are the limiting ones: the ordinary-answer
maximum stays under 2 ms through 50 players and is exceeded from 100 players in
the `burst-streams`, `cold` and `pool10` scenarios (from 150 in `burst`), and
the round close exceeds 50 ms from 200 players. Larger counts also show
multi-millisecond to tens-of-millisecond maxima from garbage-collection and
scheduler pauses on the shared host (the raw `Gc` field records them per run).

**50 is the selected maximum**: the largest tested count that, together
with every smaller tested count, met every applicable budget in every scenario
and repetition. The tested counts above it were rejected for these checks:

| Players | Passed | Failed checks |
| ---: | --- | --- |
| 2 | yes | — |
| 20 | yes | — |
| 50 | yes | — |
| 100 | no | burst-streams:ordinary_answer_max, cold:ordinary_answer_max, pool10:ordinary_answer_max |
| 150 | no | burst-streams:ordinary_answer_max, burst:ordinary_answer_max, cold:ordinary_answer_max, pool10:ordinary_answer_max |
| 200 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 250 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 350 | no | burst-streams:ordinary_answer_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 500 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |

Counts between the tested ones (for example 51-99) were not measured and are
not claimed. The maximum is a measured result on this host, not a guarantee for
other hardware; the benchmark should be re-run before raising it. The 2-player
`pool10` ordinary-answer maximum (1.7 ms) sits close to the 2 ms budget, which
shows the noise floor of the shared host.
