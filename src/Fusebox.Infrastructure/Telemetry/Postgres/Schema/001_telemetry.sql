-- Request telemetry. Every statement is idempotent, so the script is safe to run on every startup.
-- Money is whole micro-dollars (1 USD = 1,000,000), the same unit the gateway counts in.

-- One row per request. Partitioned by day: the gateway creates each day's partition (requests_yyyymmdd) just before
-- the first record for that day is saved, and drops whole partitions once they pass the retention period, which is
-- instant compared with deleting rows. Prompts and responses are never stored, only metadata and token counts.
CREATE TABLE IF NOT EXISTS requests (
    id               uuid        NOT NULL,
    started_at       timestamptz NOT NULL,
    tenant_id        text        NOT NULL,
    route_id         text        NOT NULL,
    model            text,
    -- forwarded | denied | rejected | unavailable | failed
    disposition      text        NOT NULL,
    -- budget_exceeded | request_exceeds_budget | unknown_model, only when denied
    denial_reason    text,
    status_code      smallint    NOT NULL,
    duration_ms      integer     NOT NULL,
    is_streaming     boolean     NOT NULL,
    -- Null when the vendor reported no usage.
    input_tokens     bigint,
    output_tokens    bigint,
    cost_micro_usd     bigint    NOT NULL,
    reserved_micro_usd bigint    NOT NULL,
    -- The partition key must be part of the primary key. A repeated save of the same record is ignored.
    PRIMARY KEY (id, started_at)
) PARTITION BY RANGE (started_at);

CREATE INDEX IF NOT EXISTS requests_tenant_time ON requests (tenant_id, started_at DESC);

-- Totals per bucket, tenant, route and model, computed from requests. Dashboards and detectors read these, not the
-- raw rows. Kept long after the raw rows are gone. An empty model means the request had none.
-- duration_buckets is a latency histogram of the forwarded calls, one count per bucket (see LatencyBuckets in the
-- application), from which percentiles can be estimated without the raw rows.
CREATE TABLE IF NOT EXISTS rollups_minute (
    bucket           timestamptz NOT NULL,
    tenant_id        text        NOT NULL,
    route_id         text        NOT NULL,
    model            text        NOT NULL,
    requests         bigint      NOT NULL,
    forwarded        bigint      NOT NULL,
    denied           bigint      NOT NULL,
    rejected         bigint      NOT NULL,
    unavailable      bigint      NOT NULL,
    failed           bigint      NOT NULL,
    -- Forwarded calls the vendor answered with an error status (400 and above).
    errors           bigint      NOT NULL,
    input_tokens     bigint      NOT NULL,
    output_tokens    bigint      NOT NULL,
    cost_micro_usd   bigint      NOT NULL,
    duration_ms_sum  bigint      NOT NULL,
    duration_ms_max  integer     NOT NULL,
    duration_buckets bigint[]    NOT NULL,
    PRIMARY KEY (bucket, tenant_id, route_id, model)
);

CREATE TABLE IF NOT EXISTS rollups_hour (LIKE rollups_minute INCLUDING ALL);
