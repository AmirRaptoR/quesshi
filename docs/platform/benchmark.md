# Live-path benchmark

This is a measured baseline, not a performance gate and not a change to the
`live` player limit. The complete machine-readable result is
[`benchmark.raw.json`](benchmark.raw.json).

## Reproducing the run

The checked-in raw report was generated on 2026-10-15 09:48 UTC from source
revision `cc2b442b8b3ebf3a87f43d38ec4f8e887e39a77c` on `vps-c4d96c06`:

```text
dotnet run --project platform/bench/Quesshi.Bench.Live -- --matrix
```

The environment was .NET 10.0.12 and PostgreSQL 18.6 (Debian
18.6-1.pgdg13+2). Each case used two in-process Orleans silos and one
PostgreSQL database. Clients authenticate as guests, send answers over the
production HTTP command endpoint, and the stream case consumes the production
SSE endpoint and shared renderer. Every case creates its own tenant/session
fixture with production-sized `choice` snapshots.

The matrix covers 20, 100, 250, and 500 players for:

- `burst`: answers scheduled during the final 250 ms of the question;
- `spread`: answers spread across the question;
- `burst-streams`: burst with 500 open SSE consumers;
- `cold`: activation from 100 persisted items; and
- `pool10`: burst with the application pool limited to 10 connections.

All durations below are milliseconds. “Ordinary”, “close”, and “finish” are
their maximum measured grain-occupied turn times. Command is client-observed
p95 latency, timer and pool are maxima, drain is the last accepted write
relative to deadline, and `refused` counts answers received by the deadline
but rejected because the slot had already closed. `—` is an explicitly
unavailable sample; its reason, together with every sample count and
p50/p95/p99/max distribution, is in the raw report.

| Players | Scenario | Streams / items / pool | Ordinary max | Close max | Finish max | Command p95 | Timer max | Pool max | Drain | Refused | Views | Fail / timeout |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 20 | burst | 0 / 1 / 40 | 205.6023 | 167.1919 | 166.3833 | 877.0021 | 93.2834 | 32.2012 | -40.3290 | 0 | 0 | 0 / 0 |
| 100 | burst | 0 / 1 / 40 | 214.5752 | 508.0826 | 394.5631 | 2718.1533 | 1003.2901 | 219.3871 | 2019.7960 | 0 | 0 | 0 / 0 |
| 250 | burst | 0 / 1 / 40 | 256.0205 | 924.0969 | — | 3173.4585 | 11.7299 | 101.2368 | 2019.0650 | 0 | 0 | 0 / 0 |
| 500 | burst | 0 / 1 / 40 | 259.2805 | 1827.1653 | — | 4139.6548 | 12.4452 | 137.2336 | 2060.4810 | 0 | 0 | 0 / 0 |
| 20 | spread | 0 / 1 / 40 | 55.1346 | 96.6511 | 62.1944 | 86.6756 | 227.7014 | 0.0565 | -226.4820 | 0 | 0 | 0 / 0 |
| 100 | spread | 0 / 1 / 40 | 99.7100 | 276.2762 | — | 100.8565 | 251.4811 | 0.1862 | -246.9840 | 0 | 0 | 0 / 0 |
| 250 | spread | 0 / 1 / 40 | 186.1153 | 461.1563 | — | 78.5508 | 0.4385 | 132.5669 | -243.0250 | 0 | 0 | 0 / 0 |
| 500 | spread | 0 / 1 / 40 | 111.8104 | 1278.9176 | 1254.2023 | 311.4741 | 0.1330 | 55.8016 | -231.2460 | 0 | 0 | 0 / 0 |
| 20 | burst-streams | 500 / 1 / 40 | 701.1633 | 134.8774 | 36.3107 | 1073.2850 | 475.7044 | 44.3441 | 571.6520 | 0 | 1750 | 0 / 0 |
| 100 | burst-streams | 500 / 1 / 40 | 130.7567 | 188.5883 | 212.5632 | 2298.3798 | 597.6292 | 59.3488 | 2008.2500 | 0 | 4000 | 0 / 0 |
| 250 | burst-streams | 500 / 1 / 40 | 170.0823 | 633.6623 | 306.4409 | 3421.7090 | 64.9949 | 199.0969 | 2035.5550 | 0 | 4250 | 0 / 0 |
| 500 | burst-streams | 500 / 1 / 40 | 395.4501 | 2617.4714 | 664.4315 | 3217.1962 | 364.9187 | 158.8484 | 622.7060 | 0 | 1995 | 0 / 0 |
| 20 | cold | 0 / 100 / 40 | 56.6377 | 39.2105 | — | 316.6781 | 6.7411 | 23.2261 | -44.6620 | 0 | 0 | 0 / 0 |
| 100 | cold | 0 / 100 / 40 | 187.6996 | 233.6535 | — | 2384.1374 | 36.3570 | 133.1292 | 2009.3630 | 0 | 0 | 0 / 0 |
| 250 | cold | 0 / 100 / 40 | 158.5638 | 667.1757 | — | 2889.6706 | 61.7543 | 133.9434 | 2018.4130 | 0 | 0 | 0 / 0 |
| 500 | cold | 0 / 100 / 40 | 195.6776 | 1268.7845 | — | 3526.1140 | 66.2275 | 98.4817 | 2052.4020 | 0 | 0 | 0 / 0 |
| 20 | pool10 | 0 / 1 / 10 | 50.6804 | 34.9134 | 34.5710 | 265.5375 | 267.5510 | 30.6253 | -46.0480 | 0 | 0 | 0 / 0 |
| 100 | pool10 | 0 / 1 / 10 | 170.5032 | 154.4528 | 179.7481 | 2082.2855 | 29.0937 | 118.1136 | 2011.9170 | 0 | 0 | 0 / 0 |
| 250 | pool10 | 0 / 1 / 10 | 136.9083 | 572.7491 | 298.2355 | 2749.2586 | 326.4137 | 74.2809 | 2021.3890 | 0 | 0 | 0 / 0 |
| 500 | pool10 | 0 / 1 / 10 | 241.0770 | 1528.0184 | — | 3800.0236 | 594.2572 | 117.1604 | 2062.3060 | 0 | 0 | 0 / 0 |

For cold cases, finish is unavailable because the fixture deliberately stops
after the first loaded slot; other unavailable finishes mean no finish
transition was observed in that case. No case had a command failure or timeout.

## Budget reading

The strict budgets are ordinary-answer busy time **under 2 ms**, close time
**under 50 ms**, finish time **under 100 ms**, and burst drain by deadline plus
two seconds, with zero on-time refusals.

The highest passing count is **none**. The 20-player burst drained before its
deadline and refused zero on-time answers, but its maxima were 205.6023 ms
ordinary, 167.1919 ms close, and 166.3833 ms finish, so it fails all three
turn-time budgets. At 100, 250, and 500 players, the burst drain was also
late by 19.7960 ms, 19.0650 ms, and 60.4810 ms respectively. All burst cases
recorded zero answers received by the deadline that were refused at close.

This result is deliberately not tuned to pass. It supplies the measurement for
the owner to decide whether to retain or reduce the live-player limit, or to
design batching; it makes no engine performance-policy change.
