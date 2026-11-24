# Gateway overhead benchmark

How much time Fusebox adds to a call. Design: [ADR-0001](../adr/0001-interception-yarp-gateway.md) and [ADR-0002](../adr/0002-enforcement-state-redis.md).

## How it is measured

A stand-in vendor on the same machine answers instantly, so the difference between calling it directly and calling it through the gateway is the gateway's own overhead. Requests are sent one at a time over one keep-alive connection, 3,000 per run after 400 warm-up requests, and every configuration is run four times. A request is a chat call with a 400-character prompt, so it is read, priced, held against the budget, forwarded, and settled from the vendor's reported usage.

This measures the time added to **one call**. It does not measure how many calls per second the gateway can take, which has not been measured.

Run it yourself (it needs Python 3 and a Release build; Redis and PostgreSQL are optional):

```powershell
dotnet build src/Fusebox.Gateway -c Release
python bench/gateway_overhead.py
python bench/gateway_overhead.py --redis 127.0.0.1:6379 --postgres "Host=127.0.0.1;Username=postgres;Password=...;Database=fusebox"
```

## Results

Milliseconds. The middle figure is the median of the four runs, and the range is the lowest to the highest of them.

| Configuration | Median call (p50) | Slowest 1 in 100 (p99) |
|---|---|---|
| Direct to the vendor, no Fusebox | 1.6 (1.1 to 1.7) | 2.3 to 2.6 |
| Fusebox, budgets in memory (one instance) | 2.2 (2.1 to 2.5) | 4.3 to 5.9 |
| Fusebox, budgets in Redis | 10.0 (7.6 to 10.2) | 12.1 to 14.7 |
| Fusebox, budgets in Redis, every request recorded to PostgreSQL | 8.4 (6.5 to 10.2) | 12.8 to 15.0 |

## What to take from it

- **The gateway itself adds about half a millisecond** at the median, and about two milliseconds at the slowest 1 in 100, when the budget is kept in memory. That includes parsing the request, pricing it, holding and settling the budget, and forwarding with the vendor's key.
- **Redis adds two of its own round trips per call**, one to hold the budget and one to settle it. Here that cost about 8 ms, because Redis ran in a Docker container on Windows and a plain `PING` to it took about 3.5 ms (p50) from the same machine. On a real network between a gateway and a Redis in the same data centre the round trip is usually well under a millisecond, so expect the Redis figure to be a fraction of this. It was not measured that way.
- **Recording requests to PostgreSQL adds nothing you can see here.** The last row is not slower than the one above it, and the difference between them is smaller than the run-to-run noise. Recording is off the request path by design ([ADR-0003](../adr/0003-telemetry-pipeline-in-process-queue.md)).

## Limits of this benchmark

- **One laptop, shared.** An Intel Core i7-8665U with 8 logical cores and 16 GB, running Windows, with Docker Desktop and other programs open. Neither the client, the vendor, the gateway, Redis nor PostgreSQL had a core to themselves, so the figures are noisy: the Redis and PostgreSQL rows ranged by about 3 ms between runs.
- **The vendor is instant.** Real LLM calls take hundreds of milliseconds to seconds, so this overhead is a small part of what a caller sees. It would matter more for a fast API.
- **Small, non-streaming calls only.** Streaming responses and large bodies were not measured.
- **No concurrency.** Contention on the budget store under many simultaneous callers was not measured.
- **The client is part of every figure.** A Python client was used, which is why the direct call takes over a millisecond.
