# ADR-0007: Deliver alerts through a durable alert table, PagerDuty and Slack

- **Status:** Accepted (revised during review)
- **Date:** 2026-11-22

## Context

Alerts must reliably reach the on-call engineer when a budget is exhausted, a circuit breaker opens, an anomaly fires or Fusebox itself degrades. They must not spam, and they must be robust against restarts and brief outages of the alert channel.

## Options considered

| Option | Strengths | Weaknesses |
|---|---|---|
| Simple in-app Slack webhook | Easiest, great demo | Best-effort. Slack is a chat app, so messages get missed |
| Transactional outbox with pluggable channels | Durable, retries, safe with several instances | More parts to build |
| Prometheus Alertmanager / Grafana alerting | Grouping and silencing for free | Does not understand our incident logic, extra containers |
| **Durable alert table with severity routing (chosen)** | Robust delivery to a person, no broker | More logic than a plain webhook |

The original recommendation was a plain in-app notifier, on the grounds that alerts are re-detectable state. The requirement that alerts must reliably reach the on-call engineer changed that.

## Decision

- **Write first, then send.** An alert is saved as a `pending` row in Postgres before delivery. A dispatcher sends it and marks it `sent` only after the channel confirms acceptance. Pending alerts are retried with exponential backoff, honoring Slack rate limits.
- **Dedupe.** Each alert has a fingerprint (type, tenant or endpoint, threshold) with a unique key. It is also used as the PagerDuty dedup key, so retries and repeated detections do not create duplicate incidents. Cooldowns apply per severity. Escalation fires only when severity increases, and a "resolved" notice is sent on recovery.
- **One dispatcher at a time.** A database lock (`FOR UPDATE SKIP LOCKED`) ensures several instances do not double-send.
- **Severity routing.**

| Severity | Examples | Channel |
|---|---|---|
| Critical | Budget exhausted, breaker open, enforcement degraded because Redis is down | **PagerDuty** (pages the on-call engineer via its Events API) |
| Warning and info | 80% budget, anomaly with AI diagnosis, resolved notices | **Slack** |

- **Extensibility.** Channels sit behind an `INotifier` interface, plus a generic webhook so teams can bridge to Teams, email or Opsgenie.
- **Heartbeat.** Fusebox cannot report its own death, so it pings an external heartbeat monitor on a schedule, and the monitor alerts if the pings stop.
- **Demo defaults.** The docker compose demo uses Slack or a generic webhook. PagerDuty is an optional channel enabled by config.

## Why

- Slack alone is easy to miss. PagerDuty provides on-call schedules, escalation and phone or push paging until acknowledged.
- A durable row gives outbox-style guarantees without a message broker.

## How it works

- **Sources.** One instance at a time (a database lock) looks every 15 seconds for open anomalies, anomalies that just ended, and tenants refused for budget in the last 5 minutes. Enforcement outages (degraded, fail-closed) arrive through a bounded in-memory inbox, because the code that notices them is serving requests and must never wait on a database. A writer drains the inbox and retries until the database accepts the alert.
- **Write first, send after.** Raising an alert saves the alert and one row per notification in a single transaction. A separate dispatcher claims due rows, sends them, and marks them sent. A crash between the two loses nothing: the row is still due.
- **Dedupe.** The fingerprint (`anomaly:{tenant}:{route}:{signal}`) is unique among open alerts, so two instances raising the same thing race safely, and the loser simply updates. Each notification is unique per alert, event, severity, channel and revision, so saving it twice is harmless.
- **Quiet by design.** An alert pages again only when it gets more severe. A new diagnosis updates the same alert (Slack gets the explanation; the page is not repeated). After an alert resolves, a cooldown (5 minutes critical, 15 warning) stops a flapping signal from paging over and over.
- **Critical means a ratio, not a guess.** An anomaly is critical when its peak is at least 10 times the baseline, otherwise a warning. A new anomaly waits up to 45 seconds for its diagnosis so the first message already says why. A diagnosis written by the LLM is labelled as AI-suggested.
- **Retries.** A failed send is retried with doubling delays from 5 seconds up to 5 minutes, and a channel's own `Retry-After` is honoured. A channel that will never accept it (a wrong key, a deleted webhook) is marked failed and logged instead of retried forever. Replies are never echoed into logs, because they can contain secrets.
- **Routing.** Configured per severity. If a routed channel is not set up, the alert falls back to whichever channel is, so a critical alert is never silently dropped because only Slack was configured.
- **Heartbeat.** The ping goes out only while the dispatcher has completed a round recently. A gateway that is up but cannot deliver alerts therefore stops pinging, and the outside monitor raises the alarm.

## Consequences

- Delivery is at-least-once, made safe by idempotent dedup keys.
- Alert state is auditable in the database.
- Checked on a real host against a local webhook: a seeded anomaly produced a critical alert, ending it produced a resolve notice, and heartbeat pings arrived. PagerDuty and Slack are tested against recorded request shapes and a stub server, not the live services, so a first real send should be watched.
- Budget alerts come from refused requests, so there is no "80% used" early warning yet.
- **A vendor being cut off raises a critical alert** (see ADR-0001's notes on the upstream breaker). It is named after the vendor, so two vendors have two alerts, and it resolves with a note on how long the vendor was out when a test call succeeds. Checked in the demo stack: the stand-in vendor was made to fail, the alert was raised within seconds of the circuit opening, and it resolved when the vendor came back.

## Revisit when

- Exactly-once or audited delivery, per-user routing or on-call schedules beyond PagerDuty are needed.
