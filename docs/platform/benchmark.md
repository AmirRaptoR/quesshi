# Live-path benchmark

This is a measured baseline, not a performance gate and not a change to the
`live` player limit. The complete machine-readable result, including sample
counts, p50/p95/p99/max values, units, unavailable-sample reasons, and command
outcomes, is [`benchmark.raw.json`](benchmark.raw.json).

## Reproducing the run

The checked-in raw report was generated on 2026-10-15 12:47 UTC from source
revision `510797e8bf1086de771843edd81d3a863eb347c0` on `vps-c4d96c06`:

```text
dotnet run --project platform/bench/Quesshi.Bench.Live -- --matrix --allow-disposable-postgres
```

The environment was .NET 10.0.12 and PostgreSQL 18.6 (Debian
18.6-1.pgdg13+2). The explicit switch acknowledges that the target PostgreSQL
server is disposable: the runner provisions its benchmark roles there. Each
case used two in-process Orleans silos and one PostgreSQL database. Clients
authenticate as guests, send answers over the production HTTP command endpoint,
and the stream case consumes the production SSE endpoint and shared renderer.
Every case creates its own tenant/session fixture with production-sized
`choice` snapshots.

The matrix covers 20, 100, 250, and 500 players for `burst` (answers scheduled
in the final 250 ms), `spread`, `burst-streams` (500 open SSE consumers),
`cold` (activation from 100 persisted items), and `pool10` (burst with a
10-connection application pool).

All values below are milliseconds. Turn columns are maxima; command is
client-observed p95; timer and pool are maxima; drain is the last committed
accepted answer relative to deadline. `—` is explicitly unavailable in the raw
report with its reason. `Outcome` is accepted/sent plus stale-position and
other failed command counts; failures include those non-accepted outcomes.

| Players | Scenario | Streams/items/pool | Ordinary max | Close max | Finish max | Cold max | Command p95 | Timer max | Pool max | Drain | Refused | Outcome |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 20 | burst | 0/1/40 | 352.3625 | 58.9257 | 167.1369 | — | 955.0862 | 143.5498 | 140.5246 | 41.662 | 0 | 20/20; stale 0; other 0 |
| 100 | burst | 0/1/40 | 148.3716 | 274.0422 | 406.6080 | — | 2382.4537 | 339.1861 | 188.5476 | 59.091 | 0 | 60/100; stale 40; other 0 |
| 250 | burst | 0/1/40 | 200.3674 | 840.9485 | 342.1108 | — | 3089.0766 | 906.7896 | 115.2374 | 36.455 | 0 | 38/250; stale 212; other 0 |
| 500 | burst | 0/1/40 | 639.7843 | 3241.9475 | 1371.5285 | — | 7270.0288 | 269.2328 | 251.9515 | 93.225 | 0 | 12/500; stale 1; other 487 |
| 20 | spread | 0/1/40 | 98.0795 | 51.5137 | 59.3942 | — | 109.5288 | 1.3729 | 0.0295 | -234.831 | 0 | 20/20; stale 0; other 0 |
| 100 | spread | 0/1/40 | 71.8805 | 130.7351 | 175.5535 | — | 36.5369 | 0.6419 | 25.8871 | -246.269 | 0 | 100/100; stale 0; other 0 |
| 250 | spread | 0/1/40 | 51.2737 | 599.8368 | 490.0640 | — | 54.6936 | 242.9976 | 23.6745 | -245.132 | 0 | 250/250; stale 0; other 0 |
| 500 | spread | 0/1/40 | 98.8894 | 986.5283 | 965.9624 | — | 595.7631 | 627.8243 | 18.9949 | -246.846 | 0 | 500/500; stale 0; other 0 |
| 20 | burst-streams | 500/1/40 | 37.0229 | 39.3140 | 43.2971 | — | 222.9122 | 10.9333 | 0.0297 | -43.293 | 0 | 20/20; stale 0; other 0 |
| 100 | burst-streams | 500/1/40 | 120.8483 | 252.8441 | 183.2859 | — | 2354.6812 | 511.5158 | 0.6702 | -67.513 | 12 | 88/100; stale 12; other 0 |
| 250 | burst-streams | 500/1/40 | 301.3301 | 1105.2763 | — | — | 3336.8190 | 188.2633 | 0.0318 | -150.686 | 85 | 46/250; stale 204; other 0 |
| 500 | burst-streams | 500/1/40 | 305.1191 | 1935.6694 | — | — | 4468.5136 | 109.9416 | 0.4328 | -56.159 | 51 | 32/500; stale 468; other 0 |
| 20 | cold | 0/100/40 | 67.3294 | 39.9925 | — | 39.6568 | 287.6145 | 7.4679 | 39.5240 | -47.172 | 0 | 20/20; stale 0; other 0 |
| 100 | cold | 0/100/40 | 340.9364 | 408.6614 | — | 47.8313 | 2682.3061 | 317.3123 | 186.4235 | 120.350 | 0 | 37/100; stale 63; other 0 |
| 250 | cold | 0/100/40 | 209.8434 | 551.9418 | — | 36.5737 | 2741.2045 | 15.6800 | 245.1115 | 5.547 | 0 | 68/250; stale 182; other 0 |
| 500 | cold | 0/100/40 | 312.4018 | 1145.6334 | — | 48.5600 | 3389.1102 | 19.3849 | 124.7258 | 7.023 | 0 | 47/500; stale 453; other 0 |
| 20 | pool10 | 0/1/10 | 70.9816 | 99.8326 | 40.6657 | — | 416.3148 | 42.6607 | 28.5928 | -46.118 | 0 | 20/20; stale 0; other 0 |
| 100 | pool10 | 0/1/10 | 171.6073 | 273.6947 | 202.2084 | — | 2406.1063 | 43.1169 | 79.4371 | 38.198 | 0 | 60/100; stale 40; other 0 |
| 250 | pool10 | 0/1/10 | 151.0184 | 621.6156 | 354.5293 | — | 2828.8164 | 19.5829 | 104.5634 | -27.724 | 21 | 69/250; stale 181; other 0 |
| 500 | pool10 | 0/1/10 | 208.5214 | 1273.0899 | 611.5116 | — | 3505.8752 | 430.8081 | 70.9509 | -1.582 | 2 | 62/500; stale 438; other 0 |

Cold rows deliberately stop after their first loaded slot, so finish is
unavailable; their `Cold max` is the cold 100-item activation turn. The
`burst-streams` rows received 1,458, 4,734, 4,422, and 3,101 views respectively.

## Budget reading

The strict budgets are ordinary-answer busy time **under 2 ms**, close time
**under 50 ms**, finish time **under 100 ms**, and burst drain by deadline plus
two seconds, with zero on-time refusals.

The highest passing count is **none**. The 20-player burst has zero on-time
refusals and drains 41.662 ms after its deadline, within grace, but it fails all
three turn-time budgets (352.3625 ms ordinary, 58.9257 ms close, 167.1369 ms
finish). The 100-, 250-, and 500-player burst cases also exceed all relevant
turn budgets, although each records zero on-time refusals. The streamed burst
scenarios do report on-time refusals (12, 85, and 51 at 100, 250, and 500
players), so they also fail the zero-refusal condition. These failures are
reported plainly rather than tuned away.

This result supplies the measurement for the owner to decide whether to retain
or reduce the live-player limit, or to design batching; it makes no engine
performance-policy change.
