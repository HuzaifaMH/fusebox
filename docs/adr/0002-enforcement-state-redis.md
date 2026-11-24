# ADR-0002: Keep enforcement state in Redis

- **Status:** Accepted
- **Date:** 2026-11-22

## Context

Every request needs a fast answer to "does this tenant have budget and rate-limit headroom?". Counters must stay correct under heavy concurrency and across several Fusebox instances.

Without atomic updates, two simultaneous requests can both read "$0.10 left", both pass, and overshoot the budget.

## Options considered

| Option | Strengths | Weaknesses |
|---|---|---|
| **Redis with atomic Lua scripts (chosen)** | One shared budget across all instances. Check, compare and reserve happen as one indivisible step | One more container and a critical dependency (see ADR-0008) |
| In-process counters synced to Postgres | Nanosecond reads, no extra dependency | Each instance sees only its own spend. Overshoot grows with instance count, sync interval and request rate. Budget "slicing" bounds it but adds complexity |

## Decision

Use **Redis** for live counters and cached config. A Lua script atomically checks the budget, reserves the estimated cost and returns allow, fall back or block in one round trip. Keep it behind an `IEnforcementStore` interface, with an in-memory implementation for local development and tests.

### Two kinds of data

| | Config (limits, budgets, price catalog, tenant keys, policies) | Live counters (spend, in-flight reservations) |
|---|---|---|
| Source of truth | A Postgres config table | Redis |
| Refreshed from Postgres periodically | **Yes** | **No** |

Overwriting counters from the database would reset live spend and drop in-flight reservations, so the refresh loads config only.

### Config refresh

- A background job reloads config from Postgres into Redis every **X minutes** (default 5, configurable).
- The admin API writes to Postgres and updates Redis immediately. The periodic refresh is a safety net.
- New config is written under a version key and switched in one step, so a request never sees a half-updated config.
- Each instance serves config from an **in-memory snapshot** that is replaced whole, so a request never waits on Postgres or Redis for it and a Redis outage does not stop authentication or pricing (ADR-0008). The snapshot is filled before the gateway accepts traffic: from Postgres, or from the Redis copy if Postgres is down. If neither can be read, the gateway does not start.
- Instances check the Redis version pointer every few seconds (one cheap read) and load a newer version when another instance published one, so a change reaches all instances within seconds. Only a version published after the last one seen is adopted, so a stale copy cannot undo a fresher load.
- An unknown API key is rejected without touching Postgres or Redis. Loading on demand for an unknown key would let anyone sending random keys generate database queries. A new tenant appears within the refresh interval, or at once when the admin API pushes it.
- An hourly reconciliation compares Redis spend with recorded spend in Postgres. It **alerts on mismatch and never overwrites**.
- If Redis loses data, config is reloaded from Postgres and counters are rebuilt from recorded spend. That rebuild is accurate to within the telemetry loss window (ADR-0003).

## Why

- Correctness is the product. "Budgets hold under concurrent load across instances" is demonstrable with a load test showing zero overshoot.
- The reservation approach in ADR-0010 needs an atomic check-and-reserve.
- Around 0.3 ms per call is negligible next to a call that takes hundreds of milliseconds.

## Consequences

- Redis becomes a critical dependency. Run it with a replica, put a short timeout (about 50 ms) and a circuit breaker in front of it, and follow the failure policy in ADR-0008.
- Budget changes can take up to X minutes to appear unless pushed immediately by the admin API.

## Revisit when

- Fusebox will only ever run as a single instance, or approximate enforcement is acceptable.
