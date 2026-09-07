-- Preserve every representable .NET decimal amount. A fixed scale rounded small positive claims and bills
-- to zero. PostgreSQL unconstrained numeric accepts the complete decimal coefficient and scale from Npgsql.
-- Existing rounded values cannot be reconstructed; this migration does not invent historical usage.
ALTER TABLE budget_reservation ALTER COLUMN reserved_usd TYPE NUMERIC;
ALTER TABLE budget_reservation ALTER COLUMN settled_usd TYPE NUMERIC;

-- Reconciled has always meant a pessimistic estimate, never a provider-confirmed actual. Keep its claim in
-- reserved_usd and remove the estimate from the actual column. Existing Settled rows lack enough provenance
-- to distinguish old unknown-cost settlement from a confirmed receipt and are intentionally not rewritten.
UPDATE budget_reservation SET settled_usd = NULL WHERE state = 'Reconciled';

-- The admission envelope belongs to the immutable reservation intent. A legacy row cannot prove this value;
-- do not infer it from today's run settings. Its state can be inspected, but it cannot re-admit a changed intent.
ALTER TABLE budget_reservation ADD COLUMN IF NOT EXISTS cap_usd NUMERIC NULL;
