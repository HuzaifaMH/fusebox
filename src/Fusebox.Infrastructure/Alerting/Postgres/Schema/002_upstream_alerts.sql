-- Adds the alert kind for a vendor that was cut off for failing. Idempotent: the old check is dropped and recreated.
ALTER TABLE alerts DROP CONSTRAINT IF EXISTS alerts_kind_check;
ALTER TABLE alerts ADD CONSTRAINT alerts_kind_check
    CHECK (kind IN ('anomaly', 'budget_exhausted', 'enforcement_degraded', 'enforcement_fail_closed', 'upstream_unavailable'));
