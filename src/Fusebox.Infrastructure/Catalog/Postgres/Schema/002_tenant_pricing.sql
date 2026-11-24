-- What a tenant pays compared with the vendor's list price. Every statement is idempotent.
-- Rates are whole micro-dollars per million tokens, the same unit as model_prices.

-- A multiplier on every list price, in ten-thousandths: 10000 is list price, 8500 is 15% off.
ALTER TABLE tenants ADD COLUMN IF NOT EXISTS price_multiplier_bp integer NOT NULL DEFAULT 10000
    CHECK (price_multiplier_bp BETWEEN 1 AND 1000000);

-- A rate negotiated for one model, which replaces the list price (and the multiplier) for that tenant.
-- Like model_prices, a row applies from effective_from until a later row for the same tenant and model takes over.
CREATE TABLE IF NOT EXISTS tenant_model_prices (
    tenant_id                    text        NOT NULL REFERENCES tenants (id) ON DELETE CASCADE,
    model                        text        NOT NULL CHECK (length(btrim(model)) > 0),
    effective_from               timestamptz NOT NULL DEFAULT now(),
    input_micro_usd_per_million  bigint      NOT NULL CHECK (input_micro_usd_per_million >= 0),
    output_micro_usd_per_million bigint      NOT NULL CHECK (output_micro_usd_per_million >= 0),
    PRIMARY KEY (tenant_id, model, effective_from)
);
