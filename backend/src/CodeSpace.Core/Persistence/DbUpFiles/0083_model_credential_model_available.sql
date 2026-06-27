-- A cached, advisory AVAILABILITY (reachability) flag per credentialed model — a SEPARATE axis from capability_tier
-- (0081). A recurring probe pings each ENABLED self-hosted CUSTOM-gateway model (Provider='Custom' AND a base_url is
-- set) with a minimal completion; the endpoint RESPONDING (any HTTP status, incl. 401/429/400) marks it available=true,
-- and only a genuine no-response transport failure (connection refused / reset / DNS / client timeout) marks it false.
-- Vendor models (no custom gateway) are never pinged and stay NULL (assumed available — trust the vendor, bound the
-- live-call cost to the self-hosted gateways that actually go down). last_pinged_at gates re-probing (a 30-minute
-- window — availability is volatile, so unlike the write-once tier it re-evaluates).
--
-- This is a SOFT auto-pick hint, NOT a hard pool filter: an UNPINNED auto pick (ModelPoolSelector.SelectAsync /
-- SelectBrainRowIdAsync) PREFERS available!=false rows but falls back to the full pool if every candidate is false
-- (a maybe-dead model beats no model). NULL = never probed = preferred, so an un-probed pool is byte-identical to
-- before this column existed. Pins / brain-authored dispatch / the catalog never consult it (explicit intent wins).
ALTER TABLE model_credential_model ADD COLUMN available boolean NULL;
ALTER TABLE model_credential_model ADD COLUMN last_pinged_at timestamptz NULL;
