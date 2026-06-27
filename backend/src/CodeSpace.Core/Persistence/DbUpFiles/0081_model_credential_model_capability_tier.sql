-- A cached, advisory CAPABILITY TIER per credentialed model: the brain tiers each model id (frontier / strong /
-- basic) from the id alone in one structured call, so the supervisor / planner can allocate a stronger model to a
-- harder subtask. An opaque / renamed gateway id the brain can't recognise stays untiered (NULL -> Unknown) until a
-- later objective probe slice fills it. last_tiered_at gates re-tiering so it runs as a cached fact, never per-launch.
--
-- This is a RENDER / ALLOCATION HINT only — NOT a selection pre-filter. A per-model capability gate was deliberately
-- dropped in 0073 (supports_structured_output): selection stays capability-generic (any credentialed model is
-- selectable; the structured client degrades a weak model at the call). This column never narrows the pool.
ALTER TABLE model_credential_model ADD COLUMN capability_tier text NULL;
ALTER TABLE model_credential_model ADD COLUMN last_tiered_at timestamptz NULL;
