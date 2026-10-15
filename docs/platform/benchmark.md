# Live-path benchmark

This is a measured baseline, not a performance gate and not a change to the
`live` player limit. The complete machine-readable result, including sample
counts, p50/p95/p99/max values, units, unavailable-sample reasons, command
outcomes, command-receipt coverage, and failure causes, is
[`benchmark.raw.json`](benchmark.raw.json).

## Reproducing the run

The checked-in raw report was generated on 2026-10-15 17:25 UTC from benchmark
implementation revision `3b9c919d9754a3952c6a95268031cd20431fbcd1` (an
ancestor of this documentation commit):

```text
dotnet run --project platform/bench/Quesshi.Bench.Live -- --matrix --allow-disposable-postgres
```

The runner was hosted on `vps-c4d96c06`: Intel Core Processor (Haswell, no
TSX), 6 logical processors, 11.40 GiB available memory, Ubuntu 26.04.1 LTS,
and .NET 10.0.12. PostgreSQL 16.15 (Debian 16.15-1.pgdg13+2) was local at
127.0.0.1:55455 in an isolated disposable container. The explicit switch
acknowledges that the target PostgreSQL server is disposable: the runner
provisions benchmark roles and databases there. Each case used two in-process
Orleans silos and one PostgreSQL database. Clients authenticate as guests, send
answers over the production HTTP command endpoint, and the stream case consumes
the production SSE endpoint and shared renderer. Every case creates its own
tenant/session fixture with production-sized `choice` snapshots.

The matrix covers 20, 100, 250, and 500 players for `burst` (answers scheduled
in the final 250 ms), `spread`, `burst-streams` (500 open SSE consumers),
`cold` (activation from 100 persisted items), and `pool10` (burst with a
10-connection application pool).

All values below are milliseconds. Turn columns are maxima; command is
client-observed p95; timer and pool are maxima; drain is the latest
command-origin answer/close turn's completion timestamp relative to the
deadline. It is not the HTTP receipt timestamp. `—` is explicitly unavailable
in the raw report with its reason. Outcome is accepted/sent and stale-position
results; every non-accepted command's HTTP status and command code is retained
in the raw `FailureCauses` field. There were no commands without a server
receipt, other errors, or timeouts in this run.

| Players | Scenario | Streams/items/pool | Ordinary max | Close max | Finish max | Cold max | Command p95 | Timer max | Pool max | Drain | Refused | Outcome |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 20 | burst | 0/1/40 | 124.7228 | 86.3469 | 191.3298 | — | 658.9649 | 165.9082 | 31.4459 | 681.9715 | 0 | 20/20; stale 0 |
| 100 | burst | 0/1/40 | 307.3228 | 348.5646 | 299.5467 | — | 2363.0091 | 102.8244 | 189.0815 | 2001.5817 | 0 | 72/100; stale 28 |
| 250 | burst | 0/1/40 | 214.0911 | 1059.0304 | 1310.6454 | — | 3294.1259 | 51.3572 | 96.5302 | 2022.3081 | 45 | 55/250; stale 195 |
| 500 | burst | 0/1/40 | 280.4624 | 1594.3716 | 844.8116 | — | 3932.8265 | 68.7857 | 77.3509 | 2023.3369 | 35 | 46/500; stale 454 |
| 20 | spread | 0/1/40 | 56.6031 | 61.5526 | 41.3643 | — | 69.7016 | 240.3338 | 0.0140 | -179.5368 | 0 | 20/20; stale 0 |
| 100 | spread | 0/1/40 | 39.6456 | 171.3410 | 188.0492 | — | 47.1083 | 2.2964 | 22.5789 | -73.8723 | 0 | 100/100; stale 0 |
| 250 | spread | 0/1/40 | 55.8707 | 372.7074 | — | — | 43.7953 | 241.1713 | 16.8131 | 127.1542 | 0 | 250/250; stale 0 |
| 500 | spread | 0/1/40 | 136.6753 | 596.3836 | 606.5548 | — | 528.8603 | 0.5454 | 32.8697 | 349.2182 | 0 | 500/500; stale 0 |
| 20 | burst-streams | 500/1/40 | 99.1597 | 59.6026 | 45.5748 | — | 656.3525 | 403.2042 | 0.0239 | 654.1668 | 0 | 20/20; stale 0 |
| 100 | burst-streams | 500/1/40 | 75.8899 | 309.2506 | 230.0668 | — | 2383.4742 | 25.6052 | 0.0332 | 2008.1265 | 8 | 92/100; stale 8 |
| 250 | burst-streams | 500/1/40 | 172.5101 | 839.4816 | 326.9267 | — | 3135.4203 | 173.2885 | 0.0341 | 2049.4501 | 98 | 53/250; stale 197 |
| 500 | burst-streams | 500/1/40 | 289.1053 | 1573.0175 | — | — | 3947.7347 | 81.6630 | 0.0929 | 2020.0252 | 38 | 35/500; stale 465 |
| 20 | cold | 0/100/40 | 73.0199 | 38.8328 | — | 45.0882 | 362.6943 | 13.9917 | 53.2146 | 339.6187 | 0 | 20/20; stale 0 |
| 100 | cold | 0/100/40 | 189.4648 | 190.5916 | — | 42.1041 | 2351.8390 | 11.8198 | 66.2087 | 2011.7599 | 34 | 66/100; stale 34 |
| 250 | cold | 0/100/40 | 142.0184 | 573.2335 | — | 31.9118 | 2793.9145 | 16.2578 | 42.8441 | 2001.4717 | 71 | 56/250; stale 194 |
| 500 | cold | 0/100/40 | 339.2415 | 1169.4839 | — | 32.5901 | 3407.0640 | 106.6401 | 131.6805 | 2001.7756 | 29 | 49/500; stale 451 |
| 20 | pool10 | 0/1/10 | 39.1607 | 43.1590 | — | — | 254.0903 | 405.8268 | 25.7134 | 237.0790 | 0 | 20/20; stale 0 |
| 100 | pool10 | 0/1/10 | 107.9078 | 142.1861 | 180.4547 | — | 1952.6294 | 48.3382 | 132.0785 | 2124.1368 | 0 | 100/100; stale 0 |
| 250 | pool10 | 0/1/10 | 184.4598 | 516.1519 | 239.4787 | — | 2688.6649 | 491.1954 | 89.7167 | 2010.6123 | 0 | 79/250; stale 171 |
| 500 | pool10 | 0/1/10 | 240.3795 | 1050.8879 | 545.8872 | — | 3306.6986 | 313.3607 | 113.5403 | 2023.3530 | 5 | 59/500; stale 441 |

Cold rows deliberately stop after their first loaded slot, so finish is
unavailable; their `Cold max` is the cold 100-item activation turn. The
`burst-streams` rows received 1,741, 3,721, 4,555, and 3,444 views respectively.

## Budget reading

The strict budgets are ordinary-answer busy time **under 2 ms**, close time
**under 50 ms**, finish time **under 100 ms**, and burst drain by deadline plus
two seconds, with zero on-time refusals. “Refused” is evaluated only for
commands that reached the server and have a receipt timestamp; the table's
raw-report `CommandsWithoutServerReceipt` field is zero for every row, so there is no unclassified client
cohort in this run.

The receipt-bound burst zero-refusal result required for each player count is:
20 players **yes** (0 observed on-time refusals), 100 **yes** (0), 250 **no**
(45), and 500 **no** (35). This is
not a claim that the engine accepted every answer a client submitted on time.
The endpoint stamps receipt after it has read the request body; the benchmark
does not observe time spent queued in the shared process before that point.

The raw report shows why that distinction matters. At 500 burst, 454 of 500
commands returned `http:200:command:stale_position`, while 35 of them were
receipt-stamped by the question deadline and therefore contribute to
`OnTimeRefusedAtClose`. The client-observed p95 for that row was 3932.8265 ms.
Across the 100-, 250-, and 500-player burst-related rows, client p95 ranges
from 1952.6294 ms to 3947.7347 ms. The clients, two silos, and PostgreSQL all
shared the six-logical-processor host described above, so this queueing is part
of the measured end-to-end configuration and cannot be attributed solely to
the engine. In particular, the 20- and 100-player receipt-bound **yes** values
are not evidence that the engine would refuse zero answers arriving at the
deadline under an independently paced client load. The raw report attributes
every non-accepted command in those rows to
`http:200:command:stale_position`; it records no transport or unclassified
failures.

The highest passing count is **none**. The 20-player burst drains 681.9715 ms
after its deadline but exceeds the ordinary, close, and finish turn-time budgets
(124.7228 ms ordinary, 86.3469 ms close, and 191.3298 ms finish).
At 100, 250, and 500 players the burst drain is at or beyond the two-second
grace and the turn maxima exceed the applicable budgets; 250 and 500 also have
on-time refusals. Streamed and pool-limited cases likewise contain budget and,
where shown, refusal failures. These failures are reported plainly rather than
tuned away.

This result supplies the measurement for the owner to decide whether to retain
or reduce the live-player limit, or to design batching; it makes no engine
performance-policy change.
