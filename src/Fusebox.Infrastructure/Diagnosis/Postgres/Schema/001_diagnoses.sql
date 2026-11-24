-- The diagnosis of each anomaly: what changed, found by arithmetic on the totals, and an explanation written from it.
-- Every statement is idempotent, so the script is safe to run on every startup.
-- One row per anomaly. The evidence is kept with the explanation, so the numbers behind an alert can always be seen.

CREATE TABLE IF NOT EXISTS anomaly_diagnoses (
    anomaly_id        uuid             PRIMARY KEY REFERENCES anomalies (id) ON DELETE CASCADE,
    diagnosed_at      timestamptz      NOT NULL,
    -- Who wrote the explanation: a fixed template, or an LLM whose answer was checked against the evidence.
    source            text             NOT NULL CHECK (source IN ('template', 'llm')),
    -- The LLM that wrote it, when one did.
    model             text,
    confidence        text             NOT NULL CHECK (confidence IN ('low', 'medium', 'high')),
    diagnosis         text             NOT NULL,
    remediation       text             NOT NULL,
    -- The evidence items the explanation relies on.
    evidence_ids      text[]           NOT NULL,
    -- Why the template was used when an LLM was asked for and did not deliver.
    fallback_reason   text,
    -- The share of the change the evidence does not account for, from 0 to 1.
    unexplained_share double precision NOT NULL,
    -- True if there was too little data for the evidence to be trusted much.
    is_thin           boolean          NOT NULL,
    -- Every evidence item: id, kind, label, unit, before, now, share of change, detail.
    evidence          jsonb            NOT NULL
);

CREATE INDEX IF NOT EXISTS anomaly_diagnoses_diagnosed ON anomaly_diagnoses (diagnosed_at DESC);

-- The monthly limit on LLM explanations counts these.
CREATE INDEX IF NOT EXISTS anomaly_diagnoses_llm ON anomaly_diagnoses (diagnosed_at) WHERE source = 'llm';
