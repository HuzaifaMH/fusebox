# Anomaly detection benchmark

How quickly the detector finds a fault, and whether it cries wolf. Decision: [ADR-0005](../adr/0005-anomaly-detection-statistics.md).

## How it is measured

Synthetic traffic is replayed through the real detector (the same code that runs in the gateway, against an in-memory store) one minute at a time. The detector only ever sees minutes that have already passed.

- **Traffic:** about 100 requests a minute with 15% random variation, 1% errors, 400 ms median latency, roughly $0.20 a minute.
- **Faults** start at a known minute after at least a day of normal traffic. **Time to detect** is the time from the fault's start to the moment the anomaly is reported, including the 2-minute lag that lets the minute totals settle.
- **Seeds:** each scenario runs over five random seeds. The assertions in [the benchmark tests](../../tests/Fusebox.Application.Tests/AnomalyReplayBenchmarkTests.cs) hold for the worst of them.
- **Settings:** the defaults. Fire at 4x the baseline, end after three calm checks at 2x or less, 15-minute cooldown, a five-minute window, a baseline from the last six hours.

Run it yourself:

```powershell
dotnet test tests/Fusebox.Application.Tests --filter "FullyQualifiedName~AnomalyReplayBenchmarkTests" --logger "console;verbosity=detailed"
```

## Results

| Scenario | Detected | Time to detect |
|---|---|---|
| Cost per request x5 (a pricier model swapped in) | 5 of 5 | 5 minutes |
| Request volume x6 (an unthrottled endpoint) | 5 of 5 | 5 minutes |
| Error rate 1% to 60% | 5 of 5 | 5 minutes |
| Latency median 400 ms to 6 s | 5 of 5 | 5 minutes |
| Prompt size 20 to 800 tokens, growing over 2 hours | 5 of 5 | about 82 minutes (see below) |
| Cost per request creeping x10 over 12 hours | **0 of 5** | not detected (see below) |

| Check | Result |
|---|---|
| Steady noisy traffic, with bursts and lulls, 5 runs of 48 hours | **0 false alarms** |
| A tenant with 3 requests a minute and a 30% error rate | never reported (the volume gate) |
| A fault that lasts 3 hours | reported once, not every minute |
| After a fault ends | the anomaly ends 7 minutes later |
| A daily climb from 10 to 100 requests a minute at 09:00, with earlier weeks known | 0 alarms |
| The same climb using only the last six hours | 1 alarm (a false one) |

## What the numbers mean

- **Five minutes** is two minutes of lag plus the three minutes it takes for the median of a five-minute window to move. It is the price of ignoring a single odd minute.
- **Slow growth is caught late, by design.** A signal must also exceed its baseline by an absolute amount, so tiny numbers cannot alarm on ratio alone. For prompt size that is 500 tokens, which is why 20 to 800 tokens takes about 82 minutes: the alarm fires when the growth is large enough to matter, not when it first begins.
- **A very slow drift is not caught.** A baseline learned from the last six hours keeps adapting, so a change that takes many hours looks normal the whole way. Budgets still hold in real time, so spend stays bounded, but nothing alerts. A longer-memory baseline or a trend check would address this, and would be measured here before being adopted.
- **The hour-of-week baseline matters.** Without it, a normal daily climb looks like an anomaly. With three or more earlier weeks it does not. It takes 120 minutes of samples, which is two weeks of the same hour.
- **Synthetic data is kind.** Real traffic has heavier tails, outages and deploys. Treat these as a regression suite and a floor, not a promise.
