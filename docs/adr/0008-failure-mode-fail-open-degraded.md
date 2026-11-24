# ADR-0008: Fail open, degraded, when the budget store is down

- **Status:** Accepted
- **Date:** 2026-11-22

## Context

Every request needs a budget check in Redis (ADR-0002). If Redis is unreachable or too slow, Fusebox cannot verify the budget and must choose to reject requests (fail closed) or let them through (fail open).

A Redis outage affects **enforcement** only. A Postgres outage affects recording and config refresh, but requests keep flowing from the queue buffer and the last-known config held in memory.

## Options considered

| Option | Strengths | Weaknesses |
|---|---|---|
| Fail closed | Spend is never unverified. "No call goes out without a verified budget" is testable | A Redis blip becomes an outage of every paid-API call in the apps |
| Fail open, no limits | Apps stay up | No protection during the outage, which is when load spikes and retry storms are likeliest |
| **Fail open, degraded (chosen)** | Apps stay up and exposure is bounded | A second, weaker enforcement path to build and test |

## Decision

On Redis failure, Fusebox **keeps serving in a degraded mode**:

- **Local limiter:** each instance enforces its own in-memory share of every tenant's budget (default 25% of the limit per window), using last-known config. It is a budget share rather than a request rate, so it uses the same money accounting as normal operation. With N instances the outage can spend up to N times that share.
- **Time cap:** after a maximum window (default 5 minutes) Fusebox switches to **fail closed**.
- **Fast detection:** a short timeout on Redis (about 50 ms) and a circuit breaker (three failures in a row open it; one test call every five seconds checks whether Redis is back), so a slow Redis does not add latency.
- **Alert at once:** the first degraded decision raises a critical alert through ADR-0007.
- **Reconcile afterward:** spend during the outage is written back to Postgres.
- **Per-route policy:** `FailOpenDegraded` (default) or `FailClosed`. Expensive routes (large LLM calls, payments) can opt into strict fail-closed. The percentage and the window are configurable.

## Why

- Fusebox should not become a new way for customers' apps to go down.
- Pure fail-open with no limits would make the product's promise empty at the moment it matters most. The degraded mode keeps exposure bounded: roughly instances × fallback rate × window × cost per call.
- Redis with a replica makes the failure state rare in the first place.

## Consequences

- Spend during an outage is unverified and approximate, and is reported honestly in alerts and reconciliation.
- A chaos test is part of the demo: kill Redis, show degraded serving and the alert, restore and show recovery and reconciliation.
- Adversarial tenants could try to trigger an outage to exploit the window, so the time cap and alerting matter.

## Revisit when

- Budgets are contractual (prepaid or regulated spend), or per-call cost is large enough that bounded exposure is still material. Switch those routes to `FailClosed`.
