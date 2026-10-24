# Live-path benchmark

This is the measured result of the batched live answer path (architecture
§7.4.1) and the source of the selected live player maximum. The complete,
machine-readable result is [`benchmark.raw.json`](benchmark.raw.json). It includes
the source revision, sample counts, turn maxima, burst drain, receipt coverage,
on-time refusals, failure causes, and per-scenario budget assessment. Its schema
and budget classification are validated by `BudgetClassificationTests` in
`platform/bench/Quesshi.Bench.Live.Tests`.

Selected live maximum: **2** players.

## Reproducing the run

Source revision `8ef0547ded62854752408b583dc2e678e216d188`, run on 2026-10-24 UTC.
That revision is an ancestor of this report and includes the current server and
SSE implementation exercised by `burst-streams`; the report and its analysis are
the only later changes.

```text
dotnet run -c Release --project platform/bench/Quesshi.Bench.Live -- --matrix --repetitions 2 --allow-disposable-postgres
```

The anonymized benchmark host had an Intel Core Processor (Haswell, no TSX), 6
logical processors, Ubuntu 26.04.1 LTS, and .NET 10.0.13. PostgreSQL 18.6 ran
locally as a disposable server. Each case uses two independently hosted Orleans
silos and one PostgreSQL database. Players run in a separate load process,
authenticate as guests, send answers over the production HTTP command endpoint,
and `burst-streams` also consumes the production SSE endpoint.

Counts: 2, 20, 50, 100, 150, 200, 250, 350 and 500 players; five scenarios;
two repetitions each (90 runs). `burst` schedules answers in the final 250 ms,
`spread` across the question, `burst-streams` adds 500 open SSE consumers,
`cold` activates from 100 persisted items, and `pool10` is a burst with a
10-connection application pool.

## Results

The raw report records every scenario/repetition. This table summarizes the
worst observed value among each count's ten runs. Ordinary is the grain's
in-memory answer turn; commit wait is from acceptance to durable batch commit.
Drain is the last accepted answer's commit relative to the deadline (negative is
before it). `—` means no applicable burst drain.

| Players | Ordinary max | Commit wait max | Close max | Finish max | Drain max | On-time refusals | No receipt | Result |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 2 | 0.668 | 55.093 | 25.244 | 24.153 | -7.158 | 0 | 0 | pass |
| 20 | 2.992 | 57.318 | 33.725 | 19.648 | -2.750 | 0 | 0 | fail |
| 50 | 2.412 | 78.599 | 23.531 | 21.682 | 5.560 | 0 | 0 | fail |
| 100 | 129.806 | 199.853 | 53.619 | 48.370 | 396.211 | 0 | 0 | fail |
| 150 | 62.037 | 150.737 | 64.403 | 35.254 | 87.402 | 0 | 0 | fail |
| 200 | 112.112 | 211.400 | 154.174 | 30.867 | 188.725 | 0 | 0 | fail |
| 250 | 165.521 | 287.851 | 161.566 | 48.218 | 261.592 | 0 | 0 | fail |
| 350 | 240.610 | 351.861 | 118.749 | 65.884 | 403.523 | 0 | 0 | fail |
| 500 | 306.562 | 515.664 | 82.989 | 73.490 | 804.446 | 0 | 0 | fail |

Budgets are ordinary answer < 2 ms, commit wait < 2000 ms, round close < 50 ms,
finish < 100 ms, drain <= 2000 ms, zero on-time refusals, and zero commands
without a server receipt. `benchmark.raw.json` is the canonical per-scenario
record, including p50/p95/p99 values, failure causes, and every individual
classification.

## Analysis

Every command in every run was accepted: no count produced an on-time refusal, a
timeout, a command without a receipt, or a recorded failure cause. The largest
burst drain was 804.446 ms and the largest commit wait was 515.664 ms, both below
their 2000 ms budgets. The drain, commit-wait, refusal, receipt, and finish
budgets pass at every tested count.

The limiting checks are the per-turn ordinary-answer maximum (< 2 ms) and
round-close maximum (< 50 ms). Both are within budget in every scenario and
repetition at 2 players. At 20 players, `burst-streams` exceeded the
ordinary-answer maximum. Every tested count above 20 has at least one
ordinary-answer or round-close maximum over budget.

The ordinary-answer measure does not include a share of batch commit time because
the grain is free during a group commit. The separate commit-wait budget bounds
the durable acknowledgement path.

**2 is the selected maximum**: the largest tested count that, together with
every smaller tested count, met every applicable budget in every scenario and
repetition, with zero on-time refusals. The tested counts above it were rejected
for these checks:

| Players | Passed | Failed checks |
| ---: | --- | --- |
| 2 | yes | — |
| 20 | no | burst-streams:ordinary_answer_max |
| 50 | no | pool10:ordinary_answer_max |
| 100 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, cold:ordinary_answer_max, pool10:ordinary_answer_max |
| 150 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, spread:ordinary_answer_max |
| 200 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 250 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max |
| 350 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, pool10:ordinary_answer_max, pool10:round_close_max, spread:ordinary_answer_max |
| 500 | no | burst-streams:ordinary_answer_max, burst-streams:round_close_max, burst:ordinary_answer_max, burst:round_close_max, cold:ordinary_answer_max, cold:round_close_max, pool10:ordinary_answer_max, pool10:round_close_max |

Counts between the tested ones were not measured and are not claimed. The maximum
is a result for this host, not a guarantee for other hardware. A higher cap needs
the same matrix re-run on dedicated hardware (or with more repetitions) where
host stalls do not dominate the worst-sample budgets.
