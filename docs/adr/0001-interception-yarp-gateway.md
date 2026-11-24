# ADR-0001: Intercept calls with a YARP gateway

- **Status:** Accepted
- **Date:** 2026-11-22

## Context

Fusebox must see every outbound call to a paid API before it is sent, so it can check budgets, rewrite requests (for example to fall back to a cheaper model) and record usage. Client apps should need almost no change: point the API base URL at Fusebox and add a tenant key.

## Options considered

| Option | How it works | Strengths | Weaknesses |
|---|---|---|---|
| **YARP gateway (chosen)** | A .NET reverse proxy built on ASP.NET Core, with custom middleware and request transforms | One language and process, no extra network hop, request-body rewriting is a few lines of C#, streams responses without buffering | We own proxy correctness: streaming, disconnects, timeouts |
| Envoy / Kong / LiteLLM | Off-the-shelf proxy extended through plugins or an external processing service | Battle-tested proxying, TLS and HA for free | Verbose config, extra hop to an external service, harder to debug. LiteLLM is Python and LLM-only |
| Client-side SDK | A .NET library wrapping outgoing HTTP calls | Simplest to build | Covers only .NET clients, can be bypassed, no central enforcement |

## Decision

Build Fusebox as a gateway on **YARP** inside ASP.NET Core. Client apps change their base URL to Fusebox (for example `/openai/v1/...`) and send a tenant key.

## Why

- Model fallback means parsing and rewriting the JSON body. In YARP that is an ordinary request transform with full access to DI, Redis and the price catalog.
- YARP streams server-sent events without buffering, so token usage can be read from the stream as it passes through.
- Performance is not the deciding factor. A 1 to 3 ms extra hop is noise against API calls of hundreds of milliseconds.
- The business logic is the interesting part, and a single .NET codebase keeps it testable end to end.

## How the upstream breaker works

- **One per vendor.** Each vendor (a proxy cluster, such as "openai") has its own circuit breaker, so one failing does not cut off another.
- **What counts as a failure.** A 5xx answer, or no answer at all (could not connect, timed out, an unreadable reply). A 4xx, including a vendor's 429, is the vendor answering, so it does not count. A refusal by Fusebox itself (over budget, bad request) and a caller hanging up say nothing about the vendor and are ignored.
- **Cut off.** After five failures in a row (configurable) calls to that vendor get an immediate 503 with `Retry-After`, and no budget is held for them, so callers do not each wait out a timeout. They are recorded as unavailable.
- **Coming back.** After 30 seconds (configurable) one call is let through. If it succeeds the circuit closes, and if it fails the vendor stays cut off for another 30 seconds.
- **No retries.** Fusebox never re-sends a call to the vendor. A call the vendor already started may already be billed, so a second attempt could be billed twice.
- **Alert and metrics.** Opening raises a critical alert (ADR-0007) and closing resolves it. The state and the number of refused calls are exported by vendor.
- **Checked** on the demo stack by making the stand-in vendor fail: the circuit opened within seconds, only periodic test calls reached the vendor while it was down, no budget was spent on refused calls, and it closed when the vendor recovered.
- **Not covered.** The state belongs to each gateway instance, so with several instances each one learns on its own. There is no per-call timeout setting beyond the proxy's default, and a vendor that answers slowly but successfully is not cut off.

## Consequences

- A home-built proxy is only credible if tested. The repo must include streaming tests, a client-disconnect test and a published latency benchmark.
- A client SDK can be added later as a convenience layer on top of the gateway.
- Fusebox is an outbound ("egress") gateway: it guards calls our own apps make to someone else's API.

## Revisit when

- Production traffic is polyglot and needs mTLS, WAF or global HA from day one.
- The audience is platform and infrastructure engineers who expect Envoy.
