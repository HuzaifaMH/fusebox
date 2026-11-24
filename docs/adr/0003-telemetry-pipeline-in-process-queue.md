# ADR-0003: Record telemetry through an in-process queue

- **Status:** Accepted
- **Date:** 2026-11-22

## Context

Each request produces a record: tenant, endpoint, latency, tokens or units, cost and status. Saving it must **never make the caller wait**. Writing to the database during the request would put a database round trip on every call, and a slow or failing database would slow or fail the app.

## Options considered

| Option | Strengths | Weaknesses |
|---|---|---|
| **In-process bounded queue + background batch writer (chosen)** | Nanosecond hand-off, no extra infrastructure, easy to test | A crash loses records still in memory (about a second's worth) |
| Message broker (Kafka or RabbitMQ) | Survives gateway crashes, supports replay and several independent consumers | Broker, consumer service and monitoring to run. The producer still needs a fallback if the broker is down |

## Decision

Requests place a record on a **bounded in-memory queue** and return immediately. A background worker saves records to Postgres in batches (for example up to 1,000 rows or one second, whichever comes first) using bulk insert.

- **Backpressure:** if the queue is full, new records are dropped and counted in a `telemetry_dropped` metric. A request is never blocked.
- **Graceful shutdown:** the queue is flushed on stop, so normal deploys lose nothing.
- **Interface:** hidden behind `ITelemetrySink` so a broker or outbox can replace it.

## Why

- Budgets and counters live in Redis (ADR-0002), so enforcement never depends on telemetry. A crash loses a little history, not the budget.
- Telemetry feeds dashboards and anomaly detection, where losing a second of samples is statistically irrelevant.
- The broker advocate's own verdict was that a broker earns its place only if telemetry is billing-grade.

## Consequences

- **Multiple instances work without coordination.** Each instance has its own queue and writer. Records are independent append-only rows, so Postgres handles the concurrent writers. Dashboards and detectors read from Postgres, never from queues.
- A hard crash can lose that instance's unsaved records. This loss window is documented, measured by the drop metric, and also bounds the accuracy of a Redis counter rebuild.
- Each instance holds a few Postgres connections, so connection pooling is required.
- No global ordering or replay is available.

## Revisit when

- Telemetry becomes a financial or audit record.
- Several independent consumers (billing, alerting, a warehouse) need the same event stream.
- Volume outgrows a single writer per instance.
