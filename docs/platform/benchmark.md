# Live-path benchmark

This is a measured baseline, not a performance gate and not a change to the
`live` player limit. The complete machine-readable result, including sample
counts, p50/p95/p99/max values, units, unavailable-sample reasons, command
outcomes, command-receipt coverage, and failure causes, is
[`benchmark.raw.json`](benchmark.raw.json).

## Reproducing the run

The checked-in raw report was generated on 2026-10-15 15:37 UTC from benchmark
implementation revision `9b25dd7edfa2d8cbc755f3c60b4f0e183a84b9bb` (an
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
| 20 | burst | 0/1/40 | 188.6633 | 52.9310 | 77.0603 | — | 435.8624 | 17.5410 | 36.5048 | 426.6711 | 0 | 20/20; stale 0 |
| 100 | burst | 0/1/40 | 134.4986 | 220.9234 | 132.0848 | — | 2324.5514 | 75.2869 | 98.0794 | 2010.5189 | 6 | 76/100; stale 24 |
| 250 | burst | 0/1/40 | 165.9969 | 611.1208 | — | — | 2810.1825 | 39.3804 | 89.1776 | 2013.1519 | 30 | 72/250; stale 178 |
| 500 | burst | 0/1/40 | 266.0699 | 2667.0983 | 1540.5463 | — | 4962.0441 | 45.0088 | 462.9674 | 2030.2662 | 0 | 28/500; stale 472 |
| 20 | spread | 0/1/40 | 462.9469 | 32.1377 | — | — | 854.6702 | 0.2474 | 0.1266 | -213.2419 | 0 | 20/20; stale 0 |
| 100 | spread | 0/1/40 | 63.5706 | 137.2904 | 173.9919 | — | 46.0159 | 244.1899 | 0.0350 | -108.2109 | 0 | 100/100; stale 0 |
| 250 | spread | 0/1/40 | 340.8987 | 293.8742 | 371.6634 | — | 55.7451 | 0.2083 | 21.9703 | 47.0437 | 0 | 250/250; stale 0 |
| 500 | spread | 0/1/40 | 111.2310 | 676.5329 | 659.6461 | — | 86.2202 | 570.3886 | 27.4406 | 433.4246 | 0 | 500/500; stale 0 |
| 20 | burst-streams | 500/1/40 | 84.4158 | 46.1880 | 42.9938 | — | 328.7185 | 153.7182 | 0.0261 | 312.8697 | 0 | 20/20; stale 0 |
| 100 | burst-streams | 500/1/40 | 77.8927 | 226.9609 | 158.5549 | — | 2292.5696 | 20.8181 | 0.0313 | 2011.3072 | 6 | 94/100; stale 6 |
| 250 | burst-streams | 500/1/40 | 131.4633 | 677.2613 | — | — | 2456.7979 | 1112.3521 | 0.3867 | 2003.3968 | 0 | 23/250; stale 227 |
| 500 | burst-streams | 500/1/40 | 244.9964 | 1489.8450 | 686.5219 | — | 3756.8802 | 22.8842 | 0.1457 | 2019.4467 | 55 | 39/500; stale 461 |
| 20 | cold | 0/100/40 | 171.4196 | 50.8558 | — | 42.5810 | 2252.8906 | 1525.8973 | 1718.5123 | 2017.8351 | 0 | 11/20; stale 9 |
| 100 | cold | 0/100/40 | 179.0420 | 157.4794 | — | 39.9548 | 2258.8154 | 14.9881 | 122.9362 | 2014.9087 | 2 | 77/100; stale 23 |
| 250 | cold | 0/100/40 | 679.5571 | 761.1625 | — | 37.6830 | 3192.8467 | 437.2024 | 555.7566 | 2112.5068 | 0 | 2/250; stale 248 |
| 500 | cold | 0/100/40 | 505.1801 | 4156.3133 | — | 82.3952 | 6674.8103 | 1121.1452 | 387.5169 | 2056.9085 | 0 | 16/500; stale 484 |
| 20 | pool10 | 0/1/10 | 93.5882 | 45.6943 | 43.3835 | — | 441.3685 | 364.2411 | 49.0860 | 411.4983 | 0 | 20/20; stale 0 |
| 100 | pool10 | 0/1/10 | 122.8074 | 152.6197 | 309.6944 | — | 2227.1110 | 571.2344 | 119.2356 | 2016.4997 | 8 | 92/100; stale 8 |
| 250 | pool10 | 0/1/10 | 453.3315 | 1556.9310 | — | — | 3829.7214 | 253.8847 | 262.4494 | 2039.5431 | 0 | 26/250; stale 224 |
| 500 | pool10 | 0/1/10 | 351.6792 | 1367.2320 | 768.0082 | — | 3641.6549 | 73.4570 | 103.4518 | 2023.6031 | 7 | 52/500; stale 448 |

Cold rows deliberately stop after their first loaded slot, so finish is
unavailable; their `Cold max` is the cold 100-item activation turn. The
`burst-streams` rows received 1,525, 4,022, 2,441, and 3,603 views respectively.

## Budget reading

The strict budgets are ordinary-answer busy time **under 2 ms**, close time
**under 50 ms**, finish time **under 100 ms**, and burst drain by deadline plus
two seconds, with zero on-time refusals. “Refused” is evaluated only for
commands that reached the server and have a receipt timestamp; the table's
raw-report `CommandsWithoutServerReceipt` field is zero for every row, so there is no unclassified client
cohort in this run.

The receipt-bound burst zero-refusal result required for each player count is:
20 players **yes** (0 observed on-time refusals), 100 **no** (6), 250 **no**
(30), and 500 **yes, only by the endpoint-receipt definition** (0). This is
not a claim that the engine accepted every answer a client submitted on time.
The endpoint stamps receipt after it has read the request body; the benchmark
does not observe time spent queued in the shared process before that point.

The raw report shows why that distinction matters. At 500 burst, 472 of 500
commands returned `http:200:command:stale_position`, and all 472 had been
receipt-stamped after the question deadline; consequently none contributes to
`OnTimeRefusedAtClose`. The client-observed p95 for that row was 4962.0441 ms.
Across the 100-, 250-, and 500-player burst-related rows, client p95 ranges
from 2227.1110 ms to 6674.8103 ms. The clients, two silos, and PostgreSQL all
shared the six-logical-processor host described above, so this queueing is part
of the measured end-to-end configuration and cannot be attributed solely to
the engine. In particular, the 20- and 500-player receipt-bound **yes** values
are not evidence that the engine would refuse zero answers arriving at the
deadline under an independently paced client load. The raw report attributes
every non-accepted command in those rows to
`http:200:command:stale_position`; it records no transport or unclassified
failures.

The highest passing count is **none**. The 20-player burst drains 426.6711 ms
after its deadline but exceeds the ordinary and close turn-time budgets
(188.6633 ms ordinary, 52.9310 ms close, and 77.0603 ms finish).
At 100, 250, and 500 players the burst drain is at or beyond the two-second
grace and the turn maxima exceed the applicable budgets; 100 and 250 also have
on-time refusals. Streamed and pool-limited cases likewise contain budget and,
where shown, refusal failures. These failures are reported plainly rather than
tuned away.

This result supplies the measurement for the owner to decide whether to retain
or reduce the live-player limit, or to design batching; it makes no engine
performance-policy change.
