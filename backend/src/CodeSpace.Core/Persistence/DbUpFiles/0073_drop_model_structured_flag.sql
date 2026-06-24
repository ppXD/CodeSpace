-- 0073_drop_model_structured_flag.sql
--
-- The model pool is now capability-GENERIC: it no longer gates model selection on a per-model structured-output flag.
-- Structured output is the CLIENT's job — IStructuredLLMClient degrades a model that doesn't honour forced tool-use to
-- a prompt-only JSON floor (the #534 progressive path), so any ENABLED credentialed model is selectable for any
-- in-process node (planner / decider / arbiter / schema-bearing llm.complete). A genuinely-incapable model now fails at
-- the call, never as a pre-filter — matching Dify's model-node model (no per-model capability gate). The in-code model
-- catalog that seeded this flag (and the operator env override it needed for unrecognised custom-gateway models) is
-- deleted in the same change. Drop the now-dead column (idempotent) and refresh the table's stale self-description.

ALTER TABLE model_credential_model DROP COLUMN IF EXISTS supports_structured_output;

COMMENT ON TABLE model_credential_model IS
    'A model on a model_credential''s maintained list (manual + reflected), FK-rooted under the credential so a '
    'model cannot exist without a backing key. Capability-generic — no per-model "supports X" flag (structured '
    'output is the client''s job). The team pool is the union of active credentials'' enabled rows.';
