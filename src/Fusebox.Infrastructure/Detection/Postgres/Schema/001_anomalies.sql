-- Anomalies the detector has found. Every statement is idempotent, so the script is safe to run on every startup.
-- A row is created when an anomaly starts and closed when it ends. While it is open the detector judges the signal
-- against the baseline recorded here, so a long anomaly cannot teach the detector that it is normal.

CREATE TABLE IF NOT EXISTS anomalies (
    id               uuid             PRIMARY KEY,
    tenant_id        text             NOT NULL,
    route_id         text             NOT NULL,
    -- volume | spend | error_rate | latency_p95 | input_tokens_per_request | output_tokens_per_request
    signal           text             NOT NULL,
    state            text             NOT NULL CHECK (state IN ('open', 'closed')),
    opened_at        timestamptz      NOT NULL,
    closed_at        timestamptz,
    last_seen_at     timestamptz      NOT NULL,
    -- What the baseline was learned from: hour_of_week | recent | fixed
    basis            text             NOT NULL,
    -- What is normal, in the signal's own unit. For a fixed basis, the fixed limit.
    baseline         double precision NOT NULL,
    opened_value     double precision NOT NULL,
    peak_value       double precision NOT NULL,
    -- The highest multiple of the baseline reached.
    peak_ratio       double precision NOT NULL,
    -- Evaluations in a row at or below the clear ratio. The anomaly ends when it reaches the required number.
    calm_evaluations integer          NOT NULL DEFAULT 0
);

-- At most one open anomaly per series and signal.
CREATE UNIQUE INDEX IF NOT EXISTS anomalies_one_open ON anomalies (tenant_id, route_id, signal) WHERE state = 'open';

CREATE INDEX IF NOT EXISTS anomalies_opened ON anomalies (opened_at DESC);
