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

Source revision `c05ddb23b272f622df12a34afe03a1094d1266f5`, run at 2026-10-23 17:30 UTC.
That revision is the rebased production source, including the current server and SSE
implementation exercised by `burst-streams`; this report and its analysis are the
only later changes.

```text
dotnet run -c Release --project platform/bench/Quesshi.Bench.Live -- --matrix --repetitions 2 --allow-disposable-postgres
```

The anonymized benchmark host had an Intel Core Processor (Haswell, no TSX), 6 logical processors,
Ubuntu 26.04.1 LTS, and .NET 10.0.13. PostgreSQL 18.6 ran
locally as a disposable server; the explicit switch
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
| 2 | burst | 2 | 1.1 | 17.8 | 20.6 | 24.3 | 5.5 | 0 | 0 | 2/2 | pass |
| 2 | burst-streams | 2 | 0.4 | 10.2 | 21.2 | 25.8 | -2.7 | 0 | 0 | 2/2 | pass |
| 2 | cold | 2 | 0.8 | 17.1 | 18.2 | — | -2.0 | 0 | 0 | 2/2 | pass |
| 2 | pool10 | 2 | 0.8 | 21.1 | 15.5 | 11.7 | 26.7 | 0 | 0 | 2/2 | pass |
| 2 | spread | 2 | 0.3 | 9.1 | 21.7 | 22.2 | — | 0 | 0 | 2/2 | pass |
| 20 | burst | 2 | 3.3 | 81.5 | 17.4 | 30.9 | -24.0 | 0 | 0 | 20/20 | FAIL |
| 20 | burst-streams | 2 | 0.4 | 51.7 | 22.9 | 9.8 | -12.7 | 0 | 0 | 20/20 | pass |
| 20 | cold | 2 | 0.8 | 67.9 | 48.3 | — | 19.4 | 0 | 0 | 20/20 | pass |
| 20 | pool10 | 2 | 0.3 | 36.9 | 15.2 | 11.7 | -27.2 | 0 | 0 | 20/20 | pass |
| 20 | spread | 2 | 0.4 | 72.0 | 24.0 | 21.7 | — | 0 | 0 | 20/20 | pass |
| 50 | burst | 2 | 1.2 | 81.0 | 27.0 | 15.0 | -15.1 | 0 | 0 | 50/50 | pass |
| 50 | burst-streams | 2 | 2.5 | 114.6 | 53.2 | 18.2 | 23.2 | 0 | 0 | 50/50 | FAIL |
| 50 | cold | 2 | 7.6 | 112.1 | 51.0 | — | 47.1 | 0 | 0 | 50/50 | FAIL |
| 50 | pool10 | 2 | 2.2 | 55.0 | 37.1 | 11.5 | -4.2 | 0 | 0 | 50/50 | FAIL |
| 50 | spread | 2 | 0.4 | 40.8 | 30.3 | 18.7 | — | 0 | 0 | 50/50 | pass |
| 100 | burst | 2 | 1.6 | 64.7 | 28.6 | 52.1 | 17.9 | 0 | 0 | 100/100 | pass |
| 100 | burst-streams | 2 | 3.5 | 122.0 | 56.1 | 30.8 | 44.3 | 0 | 0 | 100/100 | FAIL |
| 100 | cold | 2 | 2.1 | 103.6 | 75.5 | — | 28.8 | 0 | 0 | 100/100 | FAIL |
| 100 | pool10 | 2 | 8.8 | 123.8 | 85.7 | 20.6 | 78.7 | 0 | 0 | 100/100 | FAIL |
| 100 | spread | 2 | 26.5 | 81.2 | 31.6 | 31.6 | — | 0 | 0 | 100/100 | FAIL |
| 150 | burst | 2 | 157.6 | 267.2 | 76.1 | 24.0 | 295.1 | 0 | 0 | 150/150 | FAIL |
| 150 | burst-streams | 2 | 143.1 | 212.7 | 55.4 | 41.5 | 213.1 | 0 | 0 | 150/150 | FAIL |
| 150 | cold | 2 | 46.9 | 223.8 | 69.4 | — | 106.4 | 0 | 0 | 150/150 | FAIL |
| 150 | pool10 | 2 | 8.3 | 163.0 | 74.5 | 38.7 | 63.4 | 0 | 0 | 150/150 | FAIL |
| 150 | spread | 2 | 9.1 | 117.9 | 52.9 | 26.3 | — | 0 | 0 | 150/150 | FAIL |
| 200 | burst | 2 | 58.3 | 183.4 | 35.3 | 31.2 | 128.4 | 0 | 0 | 200/200 | FAIL |
| 200 | burst-streams | 2 | 53.6 | 249.8 | 71.0 | 31.0 | 309.3 | 0 | 0 | 200/200 | FAIL |
| 200 | cold | 2 | 25.2 | 197.7 | 54.6 | — | 155.3 | 0 | 0 | 200/200 | FAIL |
| 200 | pool10 | 2 | 71.9 | 227.9 | 63.8 | 38.9 | 130.6 | 0 | 0 | 200/200 | FAIL |
| 200 | spread | 2 | 2.0 | 70.4 | 26.1 | 21.3 | — | 0 | 0 | 200/200 | FAIL |
| 250 | burst | 2 | 76.2 | 278.0 | 97.5 | 40.6 | 328.2 | 0 | 0 | 250/250 | FAIL |
| 250 | burst-streams | 2 | 114.5 | 245.2 | 70.5 | 27.8 | 280.9 | 0 | 0 | 250/250 | FAIL |
| 250 | cold | 2 | 92.9 | 226.5 | 59.2 | — | 253.4 | 0 | 0 | 250/250 | FAIL |
| 250 | pool10 | 2 | 115.0 | 221.9 | 74.5 | 43.3 | 202.9 | 0 | 0 | 250/250 | FAIL |
| 250 | spread | 2 | 1.7 | 48.5 | 29.8 | 35.6 | — | 0 | 0 | 250/250 | pass |
| 350 | burst | 2 | 196.0 | 302.1 | 110.1 | 38.8 | 593.6 | 0 | 0 | 350/350 | FAIL |
| 350 | burst-streams | 2 | 95.7 | 292.1 | 86.3 | 49.3 | 537.0 | 0 | 0 | 350/350 | FAIL |
| 350 | cold | 2 | 186.8 | 310.4 | 78.8 | — | 440.4 | 0 | 0 | 350/350 | FAIL |
| 350 | pool10 | 2 | 183.8 | 225.5 | 93.8 | 36.9 | 418.2 | 0 | 0 | 350/350 | FAIL |
| 350 | spread | 2 | 9.1 | 83.5 | 73.9 | 50.1 | — | 0 | 0 | 350/350 | FAIL |
| 500 | burst | 2 | 206.8 | 363.5 | 153.2 | 43.7 | 803.8 | 0 | 0 | 500/500 | FAIL |
| 500 | burst-streams | 2 | 139.2 | 361.2 | 85.2 | 54.8 | 821.7 | 0 | 0 | 500/500 | FAIL |
| 500 | cold | 2 | 157.1 | 379.0 | 112.8 | — | 793.5 | 0 | 0 | 500/500 | FAIL |
| 500 | pool10 | 2 | 126.3 | 289.8 | 78.0 | 44.7 | 662.5 | 0 | 0 | 500/500 | FAIL |
| 500 | spread | 2 | 11.6 | 90.8 | 58.3 | 47.9 | — | 0 | 0 | 500/500 | FAIL |

## Analysis

Every command in every run was accepted: no count produced an on-time refusal,
a timeout or a command without a receipt (0 in total), the burst drained
822 ms after the deadline at most (budget: 2000 ms), and no answer waited
more than 379 ms for its commit (budget: 2000 ms). The earlier baseline (one
transaction per answer) accepted 46 of 500 burst answers and refused the rest;
batching removes that failure. The drain, commit-wait, refusal, receipt and finish
budgets pass at every tested count.

The limiting checks are the two per-turn maxima, which are single worst
samples and therefore sensitive to host stalls: the ordinary-answer maximum
(< 2 ms, the in-memory turn) and the round close (< 50 ms). Both are within budget
in every scenario and repetition at 2 players. At 20 players, one `burst`
sample took 3.3 ms, exceeding the ordinary-answer maximum despite every other
20-player scenario passing. From 50 players onward, at least one scenario has
an ordinary-answer or close maximum over budget.

Including the batch's commit in the ordinary-answer turn was also measured (an equal
share of the batch transaction added to each answer). A PostgreSQL commit alone takes
several milliseconds on this host, so no count, not even 2, met the 2 ms budget on
that definition. §7.4 therefore defines the ordinary-answer budget as the grain's
occupied time, because the grain is free during a group commit, and bounds the write
through the commit-wait budget above.

Earlier full runs of the same matrix on previous revisions on this host selected 50 and
20 players. They agree that 2 passes and larger counts eventually fail, but differ at
the boundary, which shows that the strict worst-sample budgets at those counts are
inside the noise of this shared host. This report is the matrix for the rebased final
production source, including the current streaming implementation.

**2 is the selected maximum**: the largest tested count that, together
with every smaller tested count, met every applicable budget in every scenario
and repetition, with zero on-time refusals. The tested counts above it were rejected for these checks:

| Players | Passed | Failed checks |
| ---: | --- | --- |
| 2 | yes | — |
| 20 | no | burst:ordinary_answer_max |
| 50 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max |
| 100 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 150 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max, spread:round_close_max |
| 200 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 250 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max |
| 350 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max, spread:round_close_max |
| 500 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max, spread:round_close_max |

Counts between the tested ones were not measured and are not claimed. The
maximum is a result for this host, not a guarantee for other hardware. A
higher cap needs the same matrix re-run on dedicated hardware (or with more
repetitions) where host stalls do not dominate the worst-sample budgets.
