-- Marks requests that repeat one sent a moment before (the signature of a retry loop), and counts them in the totals.
-- Every statement is idempotent. Totals written before this existed read as zero repeats.
ALTER TABLE requests ADD COLUMN IF NOT EXISTS is_repeat boolean NOT NULL DEFAULT false;
ALTER TABLE rollups_minute ADD COLUMN IF NOT EXISTS repeats bigint NOT NULL DEFAULT 0;
ALTER TABLE rollups_hour ADD COLUMN IF NOT EXISTS repeats bigint NOT NULL DEFAULT 0;
