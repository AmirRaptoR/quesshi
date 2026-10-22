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

Source revision `90dacb9a42eeec6337e3428086c51f1e51286562`, run at 2026-10-23 23:12 UTC.
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
| 2 | burst | 2 | 1.4 | 14.2 | 25.7 | 15.6 | -9.8 | 0 | 0 | 2/2 | pass |
| 2 | burst-streams | 2 | 1.0 | 27.1 | 37.4 | 13.9 | 19.7 | 0 | 0 | 2/2 | pass |
| 2 | cold | 2 | 0.2 | 11.2 | 22.5 | — | -17.5 | 0 | 0 | 2/2 | pass |
| 2 | pool10 | 2 | 0.7 | 10.5 | 10.0 | 12.3 | -21.5 | 0 | 0 | 2/2 | pass |
| 2 | spread | 2 | 0.3 | 19.4 | 14.4 | 23.7 | -220.2 | 0 | 0 | 2/2 | pass |
| 20 | burst | 2 | 2.1 | 38.9 | 17.4 | 15.9 | 3.9 | 0 | 0 | 20/20 | FAIL |
| 20 | burst-streams | 2 | 1.5 | 67.4 | 13.2 | 10.7 | -25.1 | 0 | 0 | 20/20 | pass |
| 20 | cold | 2 | 2.1 | 112.4 | 17.6 | — | -6.4 | 0 | 0 | 20/20 | FAIL |
| 20 | pool10 | 2 | 1.0 | 90.1 | 15.0 | 16.0 | -22.1 | 0 | 0 | 20/20 | pass |
| 20 | spread | 2 | 0.2 | 34.6 | 17.8 | 9.1 | -222.7 | 0 | 0 | 20/20 | pass |
| 50 | burst | 2 | 0.8 | 134.7 | 65.1 | 12.2 | 99.1 | 0 | 0 | 50/50 | FAIL |
| 50 | burst-streams | 2 | 2.1 | 40.2 | 16.0 | 19.7 | -1.5 | 0 | 0 | 50/50 | FAIL |
| 50 | cold | 2 | 2.8 | 80.6 | 32.1 | — | 12.8 | 0 | 0 | 50/50 | FAIL |
| 50 | pool10 | 2 | 2.2 | 114.9 | 23.1 | 24.4 | -6.1 | 0 | 0 | 50/50 | FAIL |
| 50 | spread | 2 | 0.4 | 45.8 | 12.6 | 10.2 | -232.4 | 0 | 0 | 50/50 | pass |
| 100 | burst | 2 | 17.4 | 61.9 | 19.7 | 21.9 | 6.3 | 0 | 0 | 100/100 | FAIL |
| 100 | burst-streams | 2 | 35.6 | 133.6 | 77.8 | 19.6 | 81.1 | 0 | 0 | 100/100 | FAIL |
| 100 | cold | 2 | 5.8 | 116.3 | 68.3 | — | 84.3 | 0 | 0 | 100/100 | FAIL |
| 100 | pool10 | 2 | 2.1 | 89.5 | 32.5 | 17.2 | 29.5 | 0 | 0 | 100/100 | FAIL |
| 100 | spread | 2 | 19.9 | 35.2 | 33.3 | 22.0 | -210.2 | 0 | 0 | 100/100 | FAIL |
| 150 | burst | 2 | 89.5 | 178.8 | 78.1 | 35.3 | 127.6 | 0 | 0 | 150/150 | FAIL |
| 150 | burst-streams | 2 | 3.2 | 157.3 | 90.4 | 38.2 | 141.8 | 0 | 0 | 150/150 | FAIL |
| 150 | cold | 2 | 22.8 | 167.0 | 54.7 | — | 73.9 | 0 | 0 | 150/150 | FAIL |
| 150 | pool10 | 2 | 65.9 | 160.0 | 43.1 | 24.3 | 144.9 | 0 | 0 | 150/150 | FAIL |
| 150 | spread | 2 | 1.3 | 86.7 | 22.6 | 29.7 | -200.1 | 0 | 0 | 150/150 | pass |
| 200 | burst | 2 | 101.3 | 198.6 | 43.7 | 30.0 | 220.7 | 0 | 0 | 200/200 | FAIL |
| 200 | burst-streams | 2 | 153.1 | 172.5 | 83.2 | 43.7 | 238.4 | 0 | 0 | 200/200 | FAIL |
| 200 | cold | 2 | 121.0 | 196.5 | 110.0 | — | 176.7 | 0 | 0 | 200/200 | FAIL |
| 200 | pool10 | 2 | 108.1 | 203.7 | 36.6 | 49.3 | 191.2 | 0 | 0 | 200/200 | FAIL |
| 200 | spread | 2 | 32.4 | 54.3 | 38.0 | 38.6 | -205.3 | 0 | 0 | 200/200 | FAIL |
| 250 | burst | 2 | 188.8 | 198.1 | 123.2 | 41.3 | 304.7 | 0 | 0 | 250/250 | FAIL |
| 250 | burst-streams | 2 | 396.2 | 430.1 | 64.9 | 38.8 | 633.9 | 0 | 0 | 250/250 | FAIL |
| 250 | cold | 2 | 146.8 | 257.8 | 154.2 | — | 196.6 | 0 | 0 | 250/250 | FAIL |
| 250 | pool10 | 2 | 149.1 | 245.1 | 128.8 | 32.8 | 212.1 | 0 | 0 | 250/250 | FAIL |
| 250 | spread | 2 | 8.0 | 53.6 | 25.6 | 31.3 | -215.5 | 0 | 0 | 250/250 | FAIL |
| 350 | burst | 2 | 200.9 | 321.3 | 178.0 | 76.9 | 417.2 | 0 | 0 | 350/350 | FAIL |
| 350 | burst-streams | 2 | 243.5 | 268.6 | 96.0 | 28.1 | 466.2 | 0 | 0 | 350/350 | FAIL |
| 350 | cold | 2 | 180.2 | 314.6 | 62.7 | — | 353.9 | 0 | 0 | 350/350 | FAIL |
| 350 | pool10 | 2 | 186.6 | 331.4 | 53.1 | 36.1 | 383.3 | 0 | 0 | 350/350 | FAIL |
| 350 | spread | 2 | 3.9 | 73.3 | 38.4 | 50.6 | -180.8 | 0 | 0 | 350/350 | FAIL |
| 500 | burst | 2 | 254.6 | 349.4 | 73.3 | 52.7 | 595.7 | 0 | 0 | 500/500 | FAIL |
| 500 | burst-streams | 2 | 313.9 | 575.4 | 83.8 | 50.3 | 757.5 | 0 | 0 | 500/500 | FAIL |
| 500 | cold | 2 | 350.3 | 476.9 | 74.3 | — | 674.9 | 0 | 0 | 500/500 | FAIL |
| 500 | pool10 | 2 | 286.5 | 329.3 | 66.8 | 65.2 | 697.4 | 0 | 0 | 500/500 | FAIL |
| 500 | spread | 2 | 13.2 | 85.6 | 83.2 | 56.8 | -137.8 | 0 | 0 | 500/500 | FAIL |

## Analysis

Every command in every run was accepted: no count produced an on-time refusal,
a timeout or a command without a receipt (0 in total), the burst drained
758 ms after the deadline at most (budget: 2000 ms), and no answer waited
more than 575 ms for its commit (budget: 2000 ms). The earlier baseline (one
transaction per answer) accepted 46 of 500 burst answers and refused the rest;
batching removes that failure. The drain, commit-wait, refusal, receipt and finish
budgets pass at every tested count.

The limiting checks are the two per-turn maxima, which are single worst
samples and therefore sensitive to host stalls: the ordinary-answer maximum
(< 2 ms, the in-memory turn) and the round close (< 50 ms). Both are within budget
in every scenario and repetition at 2 players. At 20 players, `burst` and `cold`
each exceeded the ordinary-answer maximum. From 50 players onward, at least one scenario has
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
| 20 | no | burst:ordinary_answer_max, cold:ordinary_answer_max |
| 50 | no | burst-streams:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, pool10:ordinary_answer_max |
| 100 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 150 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max |
| 200 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 250 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 350 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max, spread:round_close_max |
| 500 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max, spread:round_close_max |

Counts between the tested ones were not measured and are not claimed. The
maximum is a result for this host, not a guarantee for other hardware. A
higher cap needs the same matrix re-run on dedicated hardware (or with more
repetitions) where host stalls do not dominate the worst-sample budgets.
