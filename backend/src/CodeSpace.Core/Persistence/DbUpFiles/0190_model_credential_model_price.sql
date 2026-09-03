-- Per-row model pricing: the operator prices a pool model (USD per 1,000,000 input / output tokens) next to the
-- model itself, so a cost cap can be enforced for ANY provider — not only the six Claude ids the built-in table
-- carries. NULL = unpriced (the pricer then falls back to the env override table, then the built-in table, then
-- reports "unknown"). Numeric, not float: the summed bill and RouteCaps.MaxCostUsd are both decimal.
ALTER TABLE model_credential_model ADD COLUMN IF NOT EXISTS input_usd_per_million numeric NULL;
ALTER TABLE model_credential_model ADD COLUMN IF NOT EXISTS output_usd_per_million numeric NULL;
