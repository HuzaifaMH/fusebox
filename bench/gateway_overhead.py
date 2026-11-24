#!/usr/bin/env python3
"""
Measures how much time Fusebox adds to a call.

A stand-in vendor on this machine answers instantly, so the difference between calling it directly and calling it through the
gateway is the gateway's own overhead. Requests are sent one at a time over one keep-alive connection, which measures the
time added to a single call, not how many calls the gateway can take at once.

    dotnet build src/Fusebox.Gateway -c Release
    python bench/gateway_overhead.py
    python bench/gateway_overhead.py --redis 127.0.0.1:6379 --postgres "Host=127.0.0.1;Username=postgres;Password=...;Database=fusebox"

With no options it measures the gateway with budgets in memory. Each option adds a run with that dependency in use.
Python's own client time is part of every figure, which is why the direct call is shown beside the others.
"""

import argparse
import hashlib
import http.client
import http.server
import json
import os
import pathlib
import socketserver
import statistics
import subprocess
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent
UPSTREAM_PORT = 5090
GATEWAY_PORT = 5080
KEY = "bench-key"

BODY = json.dumps({"model": "bench-model", "max_tokens": 100, "messages": [{"role": "user", "content": "x" * 400}]}).encode()
REPLY = json.dumps({
    "id": "c1", "object": "chat.completion", "model": "bench-model",
    "choices": [{"index": 0, "message": {"role": "assistant", "content": "ok"}, "finish_reason": "stop"}],
    "usage": {"prompt_tokens": 100, "completion_tokens": 20, "total_tokens": 120},
}).encode()


class Vendor(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length", 0)))
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(REPLY)))
        self.end_headers()
        self.wfile.write(REPLY)

    def log_message(self, *args):
        pass


class VendorServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def handle_error(self, request, client_address):
        # The gateway is stopped between runs, which resets its connections. That is expected, not worth a traceback.
        pass


def measure(port, path, headers, warmup, count):
    connection = http.client.HTTPConnection("127.0.0.1", port)
    times = []

    for i in range(warmup + count):
        started = time.perf_counter_ns()
        connection.request("POST", path, BODY, headers)
        response = connection.getresponse()
        response.read()
        elapsed = (time.perf_counter_ns() - started) / 1e6

        if response.status != 200:
            raise RuntimeError(f"Expected 200 but got {response.status}")

        if i >= warmup:
            times.append(elapsed)

    connection.close()
    times.sort()
    return {
        "p50": times[len(times) // 2],
        "p90": times[int(len(times) * 0.90)],
        "p99": times[int(len(times) * 0.99)],
        "mean": statistics.fmean(times),
    }


def start_gateway(extra_environment):
    dll = ROOT / "src" / "Fusebox.Gateway" / "bin" / "Release" / "net10.0" / "Fusebox.Gateway.dll"
    if not dll.exists():
        raise SystemExit(f"{dll} not found. Build it first: dotnet build src/Fusebox.Gateway -c Release")

    environment = dict(os.environ)
    environment.update({
        "ASPNETCORE_URLS": f"http://127.0.0.1:{GATEWAY_PORT}",
        "ASPNETCORE_ENVIRONMENT": "Production",
        "Logging__LogLevel__Default": "Warning",
        "Fusebox__Catalog__Tenants__bench__Name": "Bench",
        "Fusebox__Catalog__Tenants__bench__ApiKeyHash": hashlib.sha256(KEY.encode()).hexdigest(),
        "Fusebox__Catalog__Tenants__bench__BudgetLimitUsd": "1000000",
        "Fusebox__Catalog__Models__bench-model__InputUsdPerMillionTokens": "1",
        "Fusebox__Catalog__Models__bench-model__OutputUsdPerMillionTokens": "2",
        "Fusebox__Upstreams__Keys__openai": "x",
        "ReverseProxy__Clusters__openai__Destinations__primary__Address": f"http://127.0.0.1:{UPSTREAM_PORT}/",
    })
    environment.update(extra_environment)

    process = subprocess.Popen(
        ["dotnet", str(dll)], env=environment, cwd=dll.parent,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    for _ in range(100):
        try:
            probe = http.client.HTTPConnection("127.0.0.1", GATEWAY_PORT, timeout=1)
            probe.request("GET", "/healthz")
            if probe.getresponse().status == 200:
                return process
        except OSError:
            time.sleep(0.3)

    process.kill()
    raise SystemExit("The gateway did not start.")


def main():
    parser = argparse.ArgumentParser(description="Measure the time Fusebox adds to a call.")
    parser.add_argument("--redis", help="host:port of a Redis to keep budgets in")
    parser.add_argument("--postgres", help="connection string of a PostgreSQL to record requests in (needs --redis)")
    parser.add_argument("--requests", type=int, default=4000, help="measured requests per run")
    parser.add_argument("--warmup", type=int, default=500, help="requests sent first and not measured")
    options = parser.parse_args()

    runs = {"Fusebox, budgets in memory (one instance)": {}}

    if options.redis:
        redis = {"Fusebox__Enforcement__Store": "Redis", "Fusebox__Enforcement__Redis__ConnectionString": options.redis}
        runs["Fusebox, budgets in Redis"] = redis

        if options.postgres:
            runs["Fusebox, budgets in Redis and every request recorded to PostgreSQL"] = {
                **redis,
                "Fusebox__Telemetry__Store": "Postgres",
                "Fusebox__Telemetry__Postgres__ConnectionString": options.postgres,
            }

    server = VendorServer(("127.0.0.1", UPSTREAM_PORT), Vendor)
    threading.Thread(target=server.serve_forever, daemon=True).start()

    results = {"Direct to the vendor (no Fusebox)": measure(UPSTREAM_PORT, "/v1/chat/completions", {"Content-Type": "application/json"}, options.warmup, options.requests)}
    headers = {"Content-Type": "application/json", "Authorization": f"Bearer {KEY}"}

    for name, environment in runs.items():
        process = start_gateway(environment)
        try:
            results[name] = measure(GATEWAY_PORT, "/openai/v1/chat/completions", headers, options.warmup, options.requests)
        finally:
            process.kill()
            process.wait()

    print(f"{'':68} {'p50':>8} {'p90':>8} {'p99':>8}   (milliseconds)")
    for name, r in results.items():
        print(f"{name:68} {r['p50']:8.2f} {r['p90']:8.2f} {r['p99']:8.2f}")


if __name__ == "__main__":
    main()
