-- Tenants and model prices: the source of truth the gateway loads its catalog from.
-- Every statement is idempotent, so the script is safe to run on every startup.
-- Money is whole micro-dollars (1 USD = 1,000,000), the same unit the gateway counts in.

CREATE TABLE IF NOT EXISTS tenants (
    id                     text        PRIMARY KEY CHECK (id ~ '^[A-Za-z0-9_-]{1,64}$'),
    name                   text        NOT NULL CHECK (length(btrim(name)) > 0),
    -- Lowercase hex SHA-256 of the tenant's API key. The key itself is never stored.
    api_key_hash           char(64)    NOT NULL UNIQUE CHECK (api_key_hash ~ '^[0-9a-f]{64}$'),
    budget_limit_micro_usd bigint      NOT NULL CHECK (budget_limit_micro_usd > 0),
    budget_window_seconds  integer     NOT NULL CHECK (budget_window_seconds >= 60),
    -- A disabled tenant is not loaded, so its calls are rejected as unauthenticated.
    enabled                boolean     NOT NULL DEFAULT true,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now()
);

-- A price applies from effective_from until a later row for the same model takes over,
-- so a price change can be scheduled ahead and history is kept.
CREATE TABLE IF NOT EXISTS model_prices (
    model                        text        NOT NULL CHECK (length(btrim(model)) > 0),
    effective_from               timestamptz NOT NULL DEFAULT now(),
    input_micro_usd_per_million  bigint      NOT NULL CHECK (input_micro_usd_per_million >= 0),
    output_micro_usd_per_million bigint      NOT NULL CHECK (output_micro_usd_per_million >= 0),
    PRIMARY KEY (model, effective_from)
);
