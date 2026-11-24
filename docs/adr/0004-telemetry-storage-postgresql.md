# ADR-0004: Store telemetry in plain PostgreSQL

- **Status:** Accepted
- **Date:** 2026-11-22

## Context

Request telemetry is queried for dashboards (spend per tenant per hour), anomaly baselines (this minute versus usual) and diagnosis (what changed). The same database also holds config (ADR-0002) and alert state (ADR-0007). Queries must not scan millions of raw rows.

## Options considered

| Option | Strengths | Weaknesses |
|---|---|---|
| **PostgreSQL with partitions and rollup tables (chosen)** | Standard image, one database for everything, first-class .NET tooling, instant retention by dropping partitions | We write and test the rollup and partition jobs ourselves |
| TimescaleDB | Still Postgres. Automatic rollups, compression and retention policies | Extension dependency and non-standard image. Some features are under a source-available license |
| ClickHouse | Excellent for huge analytical scans | A second datastore, a sync and join problem, thinner .NET tooling. Overkill here |

## Decision

Use **PostgreSQL**:

- **Raw requests table:** partitioned by day. Keep about **30 days** of raw rows.
- **Rollup tables:** per-minute and per-hour totals by tenant, endpoint and model, kept up to date by a background job using upserts. Keep **a year or more**.
- Dashboards and detectors read rollups, not raw rows.
- Storage sits behind an `ITelemetryStore` interface.

### How it works

- **Partitions are created on demand.** Just before the first record for a day is saved, the writer creates that day's partition, so a record never lands nowhere. Expired days are dropped whole, which is instant compared with deleting rows.
- **Saving is idempotent.** Records have a unique id and a repeated save is ignored, so a batch whose outcome is unknown can simply be sent again.
- **Rollups are recomputed, not added to.** Every minute the job recomputes the totals of each bucket in a recent window (15 minutes for per-minute totals, 3 hours for per-hour) from the raw rows and replaces what was there. Running it twice, or over overlapping windows, never counts a record twice, and late-arriving records inside the window are picked up. A record later than the window is missed by the totals.
- **Only one instance does each job at a time**, using a Postgres advisory lock, so several gateway instances can all run the job without repeating the work.
- **Latency percentiles** come from a fixed histogram stored with each rollup row (counts of forwarded calls per latency bucket), so a percentile can be estimated without the raw rows.

## Why

- At an organization-scale estimate of 5 million calls a day, that is about 58 rows a second on average and about 150 million rows over 30 days (very roughly 30 GB). Batched inserts, partitioning and rollups handle that comfortably.
- One standard container holds config, alerts and telemetry, with real foreign keys and joins.
- The rollup and partition design is something we can explain and benchmark.

## Consequences

- We must handle late-arriving records in the rollup job.
- Percentiles need care: store histogram buckets or a sketch in rollups, or compute them over small windows.
- TimescaleDB remains a documented drop-in upgrade, benchmarked against this design later.

## Revisit when

- Sustained ingest exceeds roughly 50,000 rows a second on one node.
- Years of raw retention or sub-second ad-hoc queries over billions of rows are needed.
