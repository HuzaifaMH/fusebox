# ADR-0005: Detect anomalies with statistics

- **Status:** Accepted
- **Date:** 2026-11-22

## Context

Fusebox must spot problems such as a 4x cost jump on one endpoint, a retry storm or payload growth, quickly, with few false alarms, and be able to say why an alert fired.

## Options considered

| Option | Strengths | Weaknesses |
|---|---|---|
| **Statistical detection (chosen)** | Works after about 20 minutes of data, every alert is explainable, pure functions are deterministic to test, near-zero cost | Needs hand-built noise controls. Weak on joint patterns across many signals |
| Machine learning (ML.NET time-series, isolation forest) | Learns per-series seasonality, can catch multi-signal patterns | Needs weeks of data, weak explanations, no labeled data to measure precision and recall |
| LLM over raw traffic | No setup | Non-deterministic, spends tokens in a tool that guards token spend, sends telemetry to a third party |

## Decision

Use **deterministic statistical detection** in C#, behind an `IAnomalyDetector` interface.

- **Baseline:** rolling median and MAD (or an exponential moving average) per tenant, endpoint and hour of week.
- **Noise controls:** a minimum-volume gate, hysteresis (fire at 4x, clear at 2x), cooldowns, and fixed fallback thresholds until the baseline has enough samples.
- **Signals for every route type:** request volume, spend per minute, retry rate, error rate and latency.
- **Extra signals for LLM routes:** input and output tokens per request, and model mix.

### How it works

- **Series and signals.** Each tenant's calls on a route form a series. Every minute, from the per-minute totals, six signals are measured: request volume, spend, error rate, 95th-percentile latency, and input and output tokens per request. Model mix is left to root-cause analysis, which can break a jump down by model.
- **Current value.** The median of the last five complete minutes (the newest minutes are held back two minutes so their totals have settled), so one odd minute cannot start or stop an anomaly.
- **Baseline.** The median and MAD of per-minute values for the same hour of the week in earlier weeks, once there are 120 minutes of samples. Until then, the last six hours. Until there are 20 minutes of data, only fixed limits for error rate and latency apply.
- **Gates.** A new anomaly needs all of: at least 20 requests in the window, at least 4x the baseline, an absolute increase of at least a per-signal minimum, and a robust z-score of at least 4 (so a noisy series needs a bigger jump). A baseline near zero is measured against a per-signal floor.
- **Lifecycle.** An open anomaly is judged against the baseline it started with, so a long one cannot teach the detector that it is normal. It ends after three checks in a row at 2x or less. After it ends, the same signal cannot start another for 15 minutes. Anomalies are kept in PostgreSQL, so a restart or a hand-over between instances loses nothing, and one instance at a time runs detection.
- **Retry rate is measured as repeated requests.** A request counts as a repeat when the same tenant sent an identical one (a fingerprint of the body, never the body) to the same route in the last minute, or when the client says it is retrying (a retry count above zero in `X-Stainless-Retry-Count`, configurable). The signal is the share of forwarded calls that repeat, and it has no fixed limit, because identical calls can be normal (a fixed prompt, a health check): only a jump from what is usual counts. In the demo, a tenant looping on one prompt reads 100% against a normal 0% and is flagged at 20 times its baseline.
- **Limits of that marker.** The memory belongs to one gateway instance, so with several instances a retry that lands on another one is missed and the rate is a lower bound. Identical requests that are not retries also count. The retry header was not checked against live vendor SDKs.
- **A new series needs history before it can be watched.** A recent baseline needs `MinRecentSamples` minutes before the window, and baselines are rebuilt every `BaselineRefreshInterval` (10 minutes), so a series that has only just appeared can go several refreshes without one. The demo shortens the refresh to two minutes for this reason.
- **Only increases.** A signal that falls (traffic stopping, for example) is not reported.

Measured results are in the [benchmark](../benchmarks/anomaly-detection.md), including what it misses.

## Why

- Every false-alarm class becomes a named, testable gate.
- There is no cold-start problem, which matters because a reviewer will run the demo on seeded data.
- An alert reads as a complete statement: "cost per minute $4.10 versus baseline $1.00, 412 requests, threshold 4x."

## Consequences

- Build a **replay benchmark**: synthetic traffic with injected faults (a 4x cost step, a retry storm, slow payload growth), reporting time-to-detect and false-positive rate. These are published results.
- An ML detector can run later as a **shadow challenger** beside the statistical one. It earns a place only if it beats the baseline on the benchmark.

## Revisit when

- Labeled incident history exists, there are thousands of tenants with non-stationary behavior, or patterns are strongly multi-seasonal.
