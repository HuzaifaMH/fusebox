-- Alerts, and the notifications waiting to be sent for them. Every statement is idempotent, so the script is safe to run
-- on every startup. An alert is written here first and sent after, so one that is raised is never lost to a restart or a
-- channel that happens to be down.

-- One row per occurrence of an incident. While it is open, raising the same fingerprint again changes nothing, which is
-- what stops a repeated detection from paging twice.
CREATE TABLE IF NOT EXISTS alerts (
    id          uuid        PRIMARY KEY,
    -- Names the incident, such as "anomaly:acme:openai-chat:spend". Also the dedupe key sent to PagerDuty.
    fingerprint text        NOT NULL,
    kind        text        NOT NULL CHECK (kind IN ('anomaly', 'budget_exhausted', 'enforcement_degraded', 'enforcement_fail_closed')),
    -- The highest severity reached while open: an alert is escalated, never downgraded.
    severity    text        NOT NULL CHECK (severity IN ('info', 'warning', 'critical')),
    status      text        NOT NULL CHECK (status IN ('open', 'resolved')),
    title       text        NOT NULL,
    summary     text        NOT NULL,
    details     jsonb       NOT NULL,
    -- Changes when the alert has something new to say, such as a diagnosis. An alert is updated only when it does.
    revision    text        NOT NULL,
    opened_at   timestamptz NOT NULL,
    updated_at  timestamptz NOT NULL,
    resolved_at timestamptz
);

-- At most one open alert per incident.
CREATE UNIQUE INDEX IF NOT EXISTS alerts_one_open ON alerts (fingerprint) WHERE status = 'open';

-- Finds when an incident last resolved, for the cooldown.
CREATE INDEX IF NOT EXISTS alerts_resolved ON alerts (fingerprint, resolved_at DESC) WHERE status = 'resolved';

-- One row per message to one channel. A row is 'sent' only after the channel confirmed it. Until then it is retried with
-- growing pauses. 'failed' means the channel will never accept it, for example because of a wrong key, and the reason is kept.
CREATE TABLE IF NOT EXISTS alert_deliveries (
    id              uuid        PRIMARY KEY,
    alert_id        uuid        NOT NULL REFERENCES alerts (id) ON DELETE CASCADE,
    -- trigger | escalate | update | resolve
    event           text        NOT NULL CHECK (event IN ('trigger', 'escalate', 'update', 'resolve')),
    severity        text        NOT NULL CHECK (severity IN ('info', 'warning', 'critical')),
    channel         text        NOT NULL,
    -- The alert's revision when this was made, so each new revision is sent once.
    revision        text        NOT NULL,
    status          text        NOT NULL CHECK (status IN ('pending', 'sent', 'failed')),
    attempts        integer     NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL,
    -- Held by the gateway instance that is sending it. If that instance dies, the hold ends and it is sent again.
    claimed_until   timestamptz,
    last_error      text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    sent_at         timestamptz,
    -- What is sent, as it was when it was made.
    payload         jsonb       NOT NULL
);

-- The same message is never queued twice for a channel.
CREATE UNIQUE INDEX IF NOT EXISTS alert_deliveries_once ON alert_deliveries (alert_id, event, severity, channel, revision);

-- The dispatcher looks only at what is waiting.
CREATE INDEX IF NOT EXISTS alert_deliveries_due ON alert_deliveries (next_attempt_at) WHERE status = 'pending';
