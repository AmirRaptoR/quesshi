# Live-path benchmark

This report establishes the published live-player ceiling for the platform's
asynchronous-duel path.

Selected live maximum: **2** players.

## Reproducing the run

The measurements were made from source revision
`6a2813d6b07bcf3b646d10ccca03a488c444b1a7` across 2026-10-24 and 2026-10-25
UTC. That revision is an ancestor of this report. Each required player count
ran in its own benchmark process; the results were then assessed together using
the checked-in budget classifier.

```sh
for count in 2 20 100 250 500; do
  dotnet run -c Release --project platform/bench/Quesshi.Bench.Live -- \
    --matrix --counts "$count" --repetitions 2 --allow-disposable-postgres \
    --output "benchmark-$count.json"
done
```

The disposable PostgreSQL instance is local to the benchmark runner. It is not
used by production or normal test execution.

## Environment

| Field | Value |
| --- | --- |
| CPU | Intel Core Processor (Haswell, no TSX) |
| Logical processors | 6 |
| Operating system | Ubuntu 26.04.1 LTS |
| .NET | 10.0.13 |
| PostgreSQL | 18.6 |

## Matrix and results

The matrix uses 2, 20, 100, 250, and 500 live players; five production-path
scenarios; and two repetitions per scenario (50 runs total). Every command was
accepted, and no run recorded a timeout, missing receipt, or failure.

| Players | Max ordinary answer (ms) | Max commit wait (ms) | Max round close (ms) | Max finish (ms) | Max burst drain (ms) | Refusals | Timeouts | Assessment |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 2 | 1.474 | 18.490 | 28.705 | 22.655 | 11.036 | 0 | 0 | pass |
| 20 | 1.271 | 126.590 | 74.094 | 22.571 | 77.706 | 0 | 0 | fail |
| 100 | 15.001 | 106.229 | 74.308 | 22.820 | 80.329 | 0 | 0 | fail |
| 250 | 213.509 | 274.014 | 230.819 | 43.520 | 278.409 | 0 | 0 | fail |
| 500 | 414.677 | 503.987 | 73.900 | 50.225 | 853.208 | 0 | 0 | fail |

The classifier allows a count only when every scenario and repetition meets
every budget. At two players, all budgets pass. At 20 players, the `pool10`
round close exceeds its 50 ms budget even though ordinary answer latency still
passes. Higher counts exceed ordinary-answer and/or round-close budgets in one
or more scenarios. Drain, commit-wait, finish, refusal, timeout, and receipt
budgets pass at every measured count.

| Candidate maximum | Status | Reason |
| ---: | --- | --- |
| 2 | selected | All five scenarios pass in both repetitions. |
| 20 | rejected | `pool10` exceeds the round-close budget. |
| 100 | rejected | Multiple scenarios exceed ordinary-answer or round-close budgets. |
| 250 | rejected | Multiple scenarios exceed ordinary-answer or round-close budgets. |
| 500 | rejected | Multiple scenarios exceed ordinary-answer or round-close budgets. |

The raw measurements and per-run budget assessments are in
[`benchmark.raw.json`](benchmark.raw.json). The live benchmark test verifies
that its selected ceiling is reproducible from that checked-in evidence.
